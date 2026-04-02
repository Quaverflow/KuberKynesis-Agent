using System.Threading.Channels;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Kube;

public sealed class KubeResourceWatchService
{
    private const int WatchTimeoutSeconds = 300;

    private readonly IKubeConfigLoader kubeConfigLoader;
    private readonly KubeResourceQueryService queryService;

    public KubeResourceWatchService(IKubeConfigLoader kubeConfigLoader, KubeResourceQueryService queryService)
    {
        this.kubeConfigLoader = kubeConfigLoader;
        this.queryService = queryService;
    }

    public async Task WatchAsync(
        KubeResourceWatchRequest request,
        Func<KubeResourceWatchMessage, CancellationToken, Task> onMessage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(onMessage);

        if (request.Contexts.Count > 1)
        {
            throw new ArgumentException("Resource watch currently supports a single kube context.");
        }

        var queryRequest = new KubeResourceQueryRequest
        {
            Kind = request.Kind,
            Contexts = request.Contexts,
            Namespace = request.Namespace,
            Search = request.Search,
            Limit = request.Limit
        };

        var initialSnapshot = await queryService.QueryAsync(queryRequest, cancellationToken);
        await onMessage(
            new KubeResourceWatchMessage(
                MessageType: KubeResourceWatchMessageType.Snapshot,
                OccurredAtUtc: DateTimeOffset.UtcNow,
                Snapshot: initialSnapshot,
                ErrorMessage: null),
            cancellationToken);

        var loadResult = kubeConfigLoader.Load();

        if (loadResult.Contexts.Count is 0)
        {
            return;
        }

        var targetContexts = KubeResourceQueryService.ResolveTargetContexts(request.Contexts, loadResult).ToArray();

        if (targetContexts.Length is 0)
        {
            return;
        }

        if (targetContexts.Length > 1)
        {
            throw new ArgumentException("Resource watch currently supports a single kube context.");
        }

        var targetContext = targetContexts[0];

        if (targetContext.Status is KubeContextStatus.ConfigurationError)
        {
            throw new ArgumentException(targetContext.StatusMessage ?? $"The kube context '{targetContext.Name}' is invalid.");
        }

        using var client = kubeConfigLoader.CreateClient(loadResult, targetContext.Name);
        var signals = CreateSignalChannel();
        var pendingEventCount = 0;
        Exception? watchFailure = null;

        using var watcher = CreateWatcher(
            client,
            request.Kind,
            request.Namespace,
            onEvent: () =>
            {
                Interlocked.Increment(ref pendingEventCount);
                signals.Writer.TryWrite(true);
            },
            onError: exception =>
            {
                watchFailure = exception;
                signals.Writer.TryComplete();
            },
            onClosed: () => signals.Writer.TryComplete(),
            cancellationToken);

        while (await signals.Reader.WaitToReadAsync(cancellationToken))
        {
            var coalescedEventCount = DrainPendingSignals(signals.Reader, ref pendingEventCount);

            if (coalescedEventCount is 0)
            {
                continue;
            }

            var snapshot = await queryService.QueryAsync(queryRequest, cancellationToken);

            await onMessage(
                new KubeResourceWatchMessage(
                    MessageType: KubeResourceWatchMessageType.Snapshot,
                    OccurredAtUtc: DateTimeOffset.UtcNow,
                    Snapshot: snapshot,
                    ErrorMessage: null,
                    CoalescedEventCount: coalescedEventCount),
                cancellationToken);
        }

        if (watchFailure is not null)
        {
            await onMessage(
                new KubeResourceWatchMessage(
                    MessageType: KubeResourceWatchMessageType.Error,
                    OccurredAtUtc: DateTimeOffset.UtcNow,
                    Snapshot: null,
                    ErrorMessage: watchFailure.Message),
                cancellationToken);

            throw watchFailure;
        }
    }

    internal static Channel<bool> CreateSignalChannel()
    {
        return Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite
        });
    }

    internal static int DrainPendingSignals(ChannelReader<bool> reader, ref int pendingEventCount)
    {
        while (reader.TryRead(out _))
        {
        }

        return Interlocked.Exchange(ref pendingEventCount, 0);
    }

    private static IDisposable CreateWatcher(
        Kubernetes client,
        KubeResourceKind kind,
        string? namespaceName,
        Action onEvent,
        Action<Exception> onError,
        Action onClosed,
        CancellationToken cancellationToken)
    {
        var coreOperations = (ICoreV1Operations)client;
        var appsOperations = (IAppsV1Operations)client;
        var batchOperations = (IBatchV1Operations)client;
        var networkingOperations = (INetworkingV1Operations)client;

        return kind switch
        {
            KubeResourceKind.Namespace => coreOperations.ListNamespaceWithHttpMessagesAsync(
                watch: true,
                timeoutSeconds: WatchTimeoutSeconds,
                cancellationToken: cancellationToken).Watch<V1Namespace, V1NamespaceList>((_, _) => onEvent(), onError, onClosed),
            KubeResourceKind.Node => coreOperations.ListNodeWithHttpMessagesAsync(
                watch: true,
                timeoutSeconds: WatchTimeoutSeconds,
                cancellationToken: cancellationToken).Watch<V1Node, V1NodeList>((_, _) => onEvent(), onError, onClosed),
            KubeResourceKind.Pod => CreateNamespacedWatcher<V1Pod, V1PodList>(
                namespaceName,
                allNamespacesFactory: () => coreOperations.ListPodForAllNamespacesWithHttpMessagesAsync(
                    watch: true,
                    timeoutSeconds: WatchTimeoutSeconds,
                    cancellationToken: cancellationToken),
                namespacedFactory: resolvedNamespace => coreOperations.ListNamespacedPodWithHttpMessagesAsync(
                    resolvedNamespace,
                    watch: true,
                    timeoutSeconds: WatchTimeoutSeconds,
                    cancellationToken: cancellationToken),
                onEvent,
                onError,
                onClosed),
            KubeResourceKind.Deployment => CreateNamespacedWatcher<V1Deployment, V1DeploymentList>(
                namespaceName,
                allNamespacesFactory: () => appsOperations.ListDeploymentForAllNamespacesWithHttpMessagesAsync(
                    watch: true,
                    timeoutSeconds: WatchTimeoutSeconds,
                    cancellationToken: cancellationToken),
                namespacedFactory: resolvedNamespace => appsOperations.ListNamespacedDeploymentWithHttpMessagesAsync(
                    resolvedNamespace,
                    watch: true,
                    timeoutSeconds: WatchTimeoutSeconds,
                    cancellationToken: cancellationToken),
                onEvent,
                onError,
                onClosed),
            KubeResourceKind.ReplicaSet => CreateNamespacedWatcher<V1ReplicaSet, V1ReplicaSetList>(
                namespaceName,
                allNamespacesFactory: () => appsOperations.ListReplicaSetForAllNamespacesWithHttpMessagesAsync(
                    watch: true,
                    timeoutSeconds: WatchTimeoutSeconds,
                    cancellationToken: cancellationToken),
                namespacedFactory: resolvedNamespace => appsOperations.ListNamespacedReplicaSetWithHttpMessagesAsync(
                    resolvedNamespace,
                    watch: true,
                    timeoutSeconds: WatchTimeoutSeconds,
                    cancellationToken: cancellationToken),
                onEvent,
                onError,
                onClosed),
            KubeResourceKind.StatefulSet => CreateNamespacedWatcher<V1StatefulSet, V1StatefulSetList>(
                namespaceName,
                allNamespacesFactory: () => appsOperations.ListStatefulSetForAllNamespacesWithHttpMessagesAsync(
                    watch: true,
                    timeoutSeconds: WatchTimeoutSeconds,
                    cancellationToken: cancellationToken),
                namespacedFactory: resolvedNamespace => appsOperations.ListNamespacedStatefulSetWithHttpMessagesAsync(
                    resolvedNamespace,
                    watch: true,
                    timeoutSeconds: WatchTimeoutSeconds,
                    cancellationToken: cancellationToken),
                onEvent,
                onError,
                onClosed),
            KubeResourceKind.DaemonSet => CreateNamespacedWatcher<V1DaemonSet, V1DaemonSetList>(
                namespaceName,
                allNamespacesFactory: () => appsOperations.ListDaemonSetForAllNamespacesWithHttpMessagesAsync(
                    watch: true,
                    timeoutSeconds: WatchTimeoutSeconds,
                    cancellationToken: cancellationToken),
                namespacedFactory: resolvedNamespace => appsOperations.ListNamespacedDaemonSetWithHttpMessagesAsync(
                    resolvedNamespace,
                    watch: true,
                    timeoutSeconds: WatchTimeoutSeconds,
                    cancellationToken: cancellationToken),
                onEvent,
                onError,
                onClosed),
            KubeResourceKind.Service => CreateNamespacedWatcher<V1Service, V1ServiceList>(
                namespaceName,
                allNamespacesFactory: () => coreOperations.ListServiceForAllNamespacesWithHttpMessagesAsync(
                    watch: true,
                    timeoutSeconds: WatchTimeoutSeconds,
                    cancellationToken: cancellationToken),
                namespacedFactory: resolvedNamespace => coreOperations.ListNamespacedServiceWithHttpMessagesAsync(
                    resolvedNamespace,
                    watch: true,
                    timeoutSeconds: WatchTimeoutSeconds,
                    cancellationToken: cancellationToken),
                onEvent,
                onError,
                onClosed),
            KubeResourceKind.Ingress => CreateNamespacedWatcher<V1Ingress, V1IngressList>(
                namespaceName,
                allNamespacesFactory: () => networkingOperations.ListIngressForAllNamespacesWithHttpMessagesAsync(
                    watch: true,
                    timeoutSeconds: WatchTimeoutSeconds,
                    cancellationToken: cancellationToken),
                namespacedFactory: resolvedNamespace => networkingOperations.ListNamespacedIngressWithHttpMessagesAsync(
                    resolvedNamespace,
                    watch: true,
                    timeoutSeconds: WatchTimeoutSeconds,
                    cancellationToken: cancellationToken),
                onEvent,
                onError,
                onClosed),
            KubeResourceKind.ConfigMap => CreateNamespacedWatcher<V1ConfigMap, V1ConfigMapList>(
                namespaceName,
                allNamespacesFactory: () => coreOperations.ListConfigMapForAllNamespacesWithHttpMessagesAsync(
                    watch: true,
                    timeoutSeconds: WatchTimeoutSeconds,
                    cancellationToken: cancellationToken),
                namespacedFactory: resolvedNamespace => coreOperations.ListNamespacedConfigMapWithHttpMessagesAsync(
                    resolvedNamespace,
                    watch: true,
                    timeoutSeconds: WatchTimeoutSeconds,
                    cancellationToken: cancellationToken),
                onEvent,
                onError,
                onClosed),
            KubeResourceKind.Secret => CreateNamespacedWatcher<V1Secret, V1SecretList>(
                namespaceName,
                allNamespacesFactory: () => coreOperations.ListSecretForAllNamespacesWithHttpMessagesAsync(
                    watch: true,
                    timeoutSeconds: WatchTimeoutSeconds,
                    cancellationToken: cancellationToken),
                namespacedFactory: resolvedNamespace => coreOperations.ListNamespacedSecretWithHttpMessagesAsync(
                    resolvedNamespace,
                    watch: true,
                    timeoutSeconds: WatchTimeoutSeconds,
                    cancellationToken: cancellationToken),
                onEvent,
                onError,
                onClosed),
            KubeResourceKind.Job => CreateNamespacedWatcher<V1Job, V1JobList>(
                namespaceName,
                allNamespacesFactory: () => batchOperations.ListJobForAllNamespacesWithHttpMessagesAsync(
                    watch: true,
                    timeoutSeconds: WatchTimeoutSeconds,
                    cancellationToken: cancellationToken),
                namespacedFactory: resolvedNamespace => batchOperations.ListNamespacedJobWithHttpMessagesAsync(
                    resolvedNamespace,
                    watch: true,
                    timeoutSeconds: WatchTimeoutSeconds,
                    cancellationToken: cancellationToken),
                onEvent,
                onError,
                onClosed),
            KubeResourceKind.CronJob => CreateNamespacedWatcher<V1CronJob, V1CronJobList>(
                namespaceName,
                allNamespacesFactory: () => batchOperations.ListCronJobForAllNamespacesWithHttpMessagesAsync(
                    watch: true,
                    timeoutSeconds: WatchTimeoutSeconds,
                    cancellationToken: cancellationToken),
                namespacedFactory: resolvedNamespace => batchOperations.ListNamespacedCronJobWithHttpMessagesAsync(
                    resolvedNamespace,
                    watch: true,
                    timeoutSeconds: WatchTimeoutSeconds,
                    cancellationToken: cancellationToken),
                onEvent,
                onError,
                onClosed),
            KubeResourceKind.Event => CreateNamespacedWatcher<Corev1Event, Corev1EventList>(
                namespaceName,
                allNamespacesFactory: () => coreOperations.ListEventForAllNamespacesWithHttpMessagesAsync(
                    watch: true,
                    timeoutSeconds: WatchTimeoutSeconds,
                    cancellationToken: cancellationToken),
                namespacedFactory: resolvedNamespace => coreOperations.ListNamespacedEventWithHttpMessagesAsync(
                    resolvedNamespace,
                    watch: true,
                    timeoutSeconds: WatchTimeoutSeconds,
                    cancellationToken: cancellationToken),
                onEvent,
                onError,
                onClosed),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported Kubernetes resource kind.")
        };
    }

    private static IDisposable CreateNamespacedWatcher<TResource, TResourceList>(
        string? namespaceName,
        Func<Task<HttpOperationResponse<TResourceList>>> allNamespacesFactory,
        Func<string, Task<HttpOperationResponse<TResourceList>>> namespacedFactory,
        Action onEvent,
        Action<Exception> onError,
        Action onClosed)
    {
        return string.IsNullOrWhiteSpace(namespaceName)
            ? allNamespacesFactory().Watch<TResource, TResourceList>((_, _) => onEvent(), onError, onClosed)
            : namespacedFactory(namespaceName.Trim()).Watch<TResource, TResourceList>((_, _) => onEvent(), onError, onClosed);
    }
}
