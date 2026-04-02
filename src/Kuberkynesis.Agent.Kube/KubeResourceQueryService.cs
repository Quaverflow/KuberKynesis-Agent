using k8s;
using Kuberkynesis.Ui.Shared.Kubernetes;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kuberkynesis.Agent.Kube;

public sealed class KubeResourceQueryService
{
    private const int DefaultLimit = 200;
    private const int MaxLimit = 500;
    private const int DefaultMaxParallelContextQueryCount = 6;
    private static readonly TimeSpan NamespaceQueryCacheDuration = TimeSpan.FromSeconds(20);
    private static readonly IReadOnlyList<KubeResourceKind> AllSupportedKinds =
    [
        KubeResourceKind.Namespace,
        KubeResourceKind.Node,
        KubeResourceKind.Pod,
        KubeResourceKind.Deployment,
        KubeResourceKind.ReplicaSet,
        KubeResourceKind.StatefulSet,
        KubeResourceKind.DaemonSet,
        KubeResourceKind.Service,
        KubeResourceKind.Ingress,
        KubeResourceKind.ConfigMap,
        KubeResourceKind.Secret,
        KubeResourceKind.Job,
        KubeResourceKind.CronJob,
        KubeResourceKind.Event
    ];

    private readonly IKubeConfigLoader kubeConfigLoader;
    private readonly ConcurrentDictionary<string, CachedContextResourceQuery> namespaceQueryCache = new(StringComparer.Ordinal);

    public KubeResourceQueryService(IKubeConfigLoader kubeConfigLoader)
    {
        this.kubeConfigLoader = kubeConfigLoader;
    }

    public async Task<KubeResourceQueryResponse> QueryAsync(KubeResourceQueryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var loadResult = kubeConfigLoader.Load();
        var normalizedLimit = NormalizeLimit(request.Limit);
        var warnings = loadResult.Warnings.Select(warning => new KubeQueryWarning(null, warning)).ToList();

        if (loadResult.Contexts.Count is 0)
        {
            return new KubeResourceQueryResponse(
                request.Kind,
                Contexts: [],
                LimitApplied: normalizedLimit,
                Resources: [],
                Warnings: warnings,
                TransparencyCommands: []);
        }

        var targetContexts = ResolveTargetContexts(request.Contexts, loadResult).ToArray();
        var targetKinds = ResolveTargetKinds(request).ToArray();
        var resources = new ConcurrentBag<KubeResourceSummary>();
        var contextWarnings = new ConcurrentQueue<KubeQueryWarning>();

        await Parallel.ForEachAsync(
            targetContexts,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Min(DefaultMaxParallelContextQueryCount, Math.Max(1, targetContexts.Length))
            },
            async (context, probeCancellationToken) =>
            {
                if (context.Status is not KubeContextStatus.Configured)
                {
                    contextWarnings.Enqueue(new KubeQueryWarning(context.Name, context.StatusMessage ?? "The kube context is not currently queryable."));
                    return;
                }

                try
                {
                    using var client = kubeConfigLoader.CreateClient(loadResult, context.Name);

                    foreach (var kind in targetKinds)
                    {
                        var kindRequest = request with
                        {
                            Kind = kind,
                            IncludeAllSupportedKinds = false,
                            CustomResourceType = kind is KubeResourceKind.CustomResource ? request.CustomResourceType : null
                        };
                        var contextResources = await QueryContextAsync(client, context.Name, kindRequest, normalizedLimit, probeCancellationToken);

                        foreach (var resource in contextResources.Where(summary => KubeResourceSummaryFactory.MatchesSearch(summary, request.Search)))
                        {
                            resources.Add(resource);
                        }
                    }
                }
                catch (Exception exception)
                {
                    contextWarnings.Enqueue(new KubeQueryWarning(context.Name, exception.Message));
                }
            });

        warnings.AddRange(contextWarnings);

        var orderedResources = resources
            .OrderBy(resource => resource.ContextName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(resource => resource.Namespace ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(resource => resource.Kind.ToString(), StringComparer.OrdinalIgnoreCase)
            .ThenBy(resource => resource.Name, StringComparer.OrdinalIgnoreCase)
            .Take(normalizedLimit)
            .ToArray();

        return new KubeResourceQueryResponse(
            request.Kind,
            Contexts: targetContexts.Select(context => context.Name).ToArray(),
            LimitApplied: normalizedLimit,
            Resources: orderedResources,
            Warnings: warnings,
            TransparencyCommands: KubectlTransparencyFactory.CreateForQuery(
                request,
                targetContexts.Select(context => context.Name).ToArray(),
                normalizedLimit));
    }

    internal static IReadOnlyList<DiscoveredKubeContext> ResolveTargetContexts(
        IReadOnlyList<string> requestedContexts,
        KubeConfigLoadResult loadResult)
    {
        if (requestedContexts.Count is 0)
        {
            return loadResult.Contexts.Where(context => context.IsCurrent).Take(1).ToArray();
        }

        var contextMap = loadResult.Contexts.ToDictionary(context => context.Name, StringComparer.Ordinal);
        var resolved = new List<DiscoveredKubeContext>(requestedContexts.Count);
        var missingContexts = new List<string>();

        foreach (var requestedContext in requestedContexts.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (contextMap.TryGetValue(requestedContext, out var context))
            {
                resolved.Add(context);
            }
            else
            {
                missingContexts.Add(requestedContext);
            }
        }

        if (missingContexts.Count > 0)
        {
            throw new ArgumentException($"Unknown kube contexts: {string.Join(", ", missingContexts)}");
        }

        return resolved;
    }

    internal static IReadOnlyList<KubeResourceKind> ResolveTargetKinds(KubeResourceQueryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.IncludeAllSupportedKinds)
        {
            return [request.Kind];
        }

        return string.IsNullOrWhiteSpace(request.Namespace)
            ? AllSupportedKinds
            : AllSupportedKinds.Where(static kind => kind is not KubeResourceKind.Namespace and not KubeResourceKind.Node).ToArray();
    }

    internal static int NormalizeLimit(int requestedLimit)
    {
        if (requestedLimit <= 0)
        {
            return DefaultLimit;
        }

        return Math.Min(requestedLimit, MaxLimit);
    }

    private Task<IReadOnlyList<KubeResourceSummary>> QueryContextAsync(
        Kubernetes client,
        string contextName,
        KubeResourceQueryRequest request,
        int limit,
        CancellationToken cancellationToken)
    {
        return request.Kind switch
        {
            KubeResourceKind.CustomResource => QueryCustomResourcesAsync(client, contextName, request, limit, cancellationToken),
            KubeResourceKind.Namespace => QueryCachedNamespacesAsync(client, contextName, limit, cancellationToken),
            KubeResourceKind.Node => QueryNodesAsync(client, contextName, limit, cancellationToken),
            KubeResourceKind.Pod => QueryPodsAsync(client, contextName, request.Namespace, limit, cancellationToken),
            KubeResourceKind.Deployment => QueryDeploymentsAsync(client, contextName, request.Namespace, limit, cancellationToken),
            KubeResourceKind.ReplicaSet => QueryReplicaSetsAsync(client, contextName, request.Namespace, limit, cancellationToken),
            KubeResourceKind.StatefulSet => QueryStatefulSetsAsync(client, contextName, request.Namespace, limit, cancellationToken),
            KubeResourceKind.DaemonSet => QueryDaemonSetsAsync(client, contextName, request.Namespace, limit, cancellationToken),
            KubeResourceKind.Service => QueryServicesAsync(client, contextName, request.Namespace, limit, cancellationToken),
            KubeResourceKind.Ingress => QueryIngressesAsync(client, contextName, request.Namespace, limit, cancellationToken),
            KubeResourceKind.ConfigMap => QueryConfigMapsAsync(client, contextName, request.Namespace, limit, cancellationToken),
            KubeResourceKind.Secret => QuerySecretsAsync(client, contextName, request.Namespace, limit, cancellationToken),
            KubeResourceKind.Job => QueryJobsAsync(client, contextName, request.Namespace, limit, cancellationToken),
            KubeResourceKind.CronJob => QueryCronJobsAsync(client, contextName, request.Namespace, limit, cancellationToken),
            KubeResourceKind.Event => QueryEventsAsync(client, contextName, request.Namespace, limit, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(request.Kind), request.Kind, "Unsupported Kubernetes resource kind.")
        };
    }

    private Task<IReadOnlyList<KubeResourceSummary>> QueryCachedNamespacesAsync(
        Kubernetes client,
        string contextName,
        int limit,
        CancellationToken cancellationToken)
    {
        var cacheKey = $"{contextName}|{limit}";
        return QueryCachedContextResourcesAsync(
            namespaceQueryCache,
            cacheKey,
            () => QueryNamespacesAsync(client, contextName, limit, cancellationToken),
            NamespaceQueryCacheDuration,
            cancellationToken);
    }

    private static async Task<IReadOnlyList<KubeResourceSummary>> QueryCachedContextResourcesAsync(
        ConcurrentDictionary<string, CachedContextResourceQuery> cache,
        string cacheKey,
        Func<Task<IReadOnlyList<KubeResourceSummary>>> loader,
        TimeSpan cacheDuration,
        CancellationToken cancellationToken)
    {
        var entry = cache.GetOrAdd(cacheKey, static _ => new CachedContextResourceQuery());
        var now = DateTimeOffset.UtcNow;

        if (entry.Resources is not null && entry.ExpiresAtUtc > now)
        {
            return entry.Resources;
        }

        await entry.Gate.WaitAsync(cancellationToken);

        try
        {
            now = DateTimeOffset.UtcNow;

            if (entry.Resources is not null && entry.ExpiresAtUtc > now)
            {
                return entry.Resources;
            }

            var resources = await loader();
            entry.Resources = resources;
            entry.ExpiresAtUtc = now.Add(cacheDuration);
            return resources;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private static async Task<IReadOnlyList<KubeResourceSummary>> QueryCustomResourcesAsync(
        Kubernetes client,
        string contextName,
        KubeResourceQueryRequest request,
        int limit,
        CancellationToken cancellationToken)
    {
        var customResourceType = request.CustomResourceType
            ?? throw new ArgumentException("A custom resource definition is required for custom resource queries.", nameof(request));
        var operations = (ICustomObjectsOperations)client;

        object response = customResourceType.Namespaced
            ? string.IsNullOrWhiteSpace(request.Namespace)
                ? await k8s.CustomObjectsOperationsExtensions.ListCustomObjectForAllNamespacesAsync(
                    operations,
                    customResourceType.Group,
                    customResourceType.Version,
                    customResourceType.Plural,
                    limit: limit,
                    cancellationToken: cancellationToken)
                : await k8s.CustomObjectsOperationsExtensions.ListNamespacedCustomObjectAsync(
                    operations,
                    customResourceType.Group,
                    customResourceType.Version,
                    request.Namespace.Trim(),
                    customResourceType.Plural,
                    limit: limit,
                    cancellationToken: cancellationToken)
            : await k8s.CustomObjectsOperationsExtensions.ListClusterCustomObjectAsync(
                operations,
                customResourceType.Group,
                customResourceType.Version,
                customResourceType.Plural,
                limit: limit,
                cancellationToken: cancellationToken);

        var root = JsonSerializer.SerializeToNode(response) as JsonObject;
        var items = root?["items"] as JsonArray;

        if (items is null)
        {
            return [];
        }

        return items
            .OfType<JsonObject>()
            .Select(item => KubeResourceSummaryFactory.Create(contextName, customResourceType, item))
            .ToArray();
    }

    private static async Task<IReadOnlyList<KubeResourceSummary>> QueryNamespacesAsync(
        Kubernetes client,
        string contextName,
        int limit,
        CancellationToken cancellationToken)
    {
        var list = await client.ListNamespaceAsync(limit: limit, cancellationToken: cancellationToken);
        return list.Items.Select(item => KubeResourceSummaryFactory.Create(contextName, item)).ToArray();
    }

    private sealed class CachedContextResourceQuery
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public DateTimeOffset ExpiresAtUtc { get; set; } = DateTimeOffset.MinValue;

        public IReadOnlyList<KubeResourceSummary>? Resources { get; set; }
    }

    private static async Task<IReadOnlyList<KubeResourceSummary>> QueryNodesAsync(
        Kubernetes client,
        string contextName,
        int limit,
        CancellationToken cancellationToken)
    {
        var list = await client.ListNodeAsync(limit: limit, cancellationToken: cancellationToken);
        return list.Items.Select(item => KubeResourceSummaryFactory.Create(contextName, item)).ToArray();
    }

    private static async Task<IReadOnlyList<KubeResourceSummary>> QueryPodsAsync(
        Kubernetes client,
        string contextName,
        string? namespaceName,
        int limit,
        CancellationToken cancellationToken)
    {
        var list = string.IsNullOrWhiteSpace(namespaceName)
            ? await client.ListPodForAllNamespacesAsync(limit: limit, cancellationToken: cancellationToken)
            : await client.ListNamespacedPodAsync(namespaceName, limit: limit, cancellationToken: cancellationToken);

        return list.Items.Select(item => KubeResourceSummaryFactory.Create(contextName, item)).ToArray();
    }

    private static async Task<IReadOnlyList<KubeResourceSummary>> QueryDeploymentsAsync(
        Kubernetes client,
        string contextName,
        string? namespaceName,
        int limit,
        CancellationToken cancellationToken)
    {
        var list = string.IsNullOrWhiteSpace(namespaceName)
            ? await client.ListDeploymentForAllNamespacesAsync(limit: limit, cancellationToken: cancellationToken)
            : await client.ListNamespacedDeploymentAsync(namespaceName, limit: limit, cancellationToken: cancellationToken);

        return list.Items.Select(item => KubeResourceSummaryFactory.Create(contextName, item)).ToArray();
    }

    private static async Task<IReadOnlyList<KubeResourceSummary>> QueryReplicaSetsAsync(
        Kubernetes client,
        string contextName,
        string? namespaceName,
        int limit,
        CancellationToken cancellationToken)
    {
        var list = string.IsNullOrWhiteSpace(namespaceName)
            ? await client.ListReplicaSetForAllNamespacesAsync(limit: limit, cancellationToken: cancellationToken)
            : await client.ListNamespacedReplicaSetAsync(namespaceName, limit: limit, cancellationToken: cancellationToken);

        return list.Items.Select(item => KubeResourceSummaryFactory.Create(contextName, item)).ToArray();
    }

    private static async Task<IReadOnlyList<KubeResourceSummary>> QueryStatefulSetsAsync(
        Kubernetes client,
        string contextName,
        string? namespaceName,
        int limit,
        CancellationToken cancellationToken)
    {
        var list = string.IsNullOrWhiteSpace(namespaceName)
            ? await client.ListStatefulSetForAllNamespacesAsync(limit: limit, cancellationToken: cancellationToken)
            : await client.ListNamespacedStatefulSetAsync(namespaceName, limit: limit, cancellationToken: cancellationToken);

        return list.Items.Select(item => KubeResourceSummaryFactory.Create(contextName, item)).ToArray();
    }

    private static async Task<IReadOnlyList<KubeResourceSummary>> QueryDaemonSetsAsync(
        Kubernetes client,
        string contextName,
        string? namespaceName,
        int limit,
        CancellationToken cancellationToken)
    {
        var list = string.IsNullOrWhiteSpace(namespaceName)
            ? await client.ListDaemonSetForAllNamespacesAsync(limit: limit, cancellationToken: cancellationToken)
            : await client.ListNamespacedDaemonSetAsync(namespaceName, limit: limit, cancellationToken: cancellationToken);

        return list.Items.Select(item => KubeResourceSummaryFactory.Create(contextName, item)).ToArray();
    }

    private static async Task<IReadOnlyList<KubeResourceSummary>> QueryServicesAsync(
        Kubernetes client,
        string contextName,
        string? namespaceName,
        int limit,
        CancellationToken cancellationToken)
    {
        var list = string.IsNullOrWhiteSpace(namespaceName)
            ? await client.ListServiceForAllNamespacesAsync(limit: limit, cancellationToken: cancellationToken)
            : await client.ListNamespacedServiceAsync(namespaceName, limit: limit, cancellationToken: cancellationToken);

        return list.Items.Select(item => KubeResourceSummaryFactory.Create(contextName, item)).ToArray();
    }

    private static async Task<IReadOnlyList<KubeResourceSummary>> QueryIngressesAsync(
        Kubernetes client,
        string contextName,
        string? namespaceName,
        int limit,
        CancellationToken cancellationToken)
    {
        var list = string.IsNullOrWhiteSpace(namespaceName)
            ? await client.ListIngressForAllNamespacesAsync(limit: limit, cancellationToken: cancellationToken)
            : await client.ListNamespacedIngressAsync(namespaceName, limit: limit, cancellationToken: cancellationToken);

        return list.Items.Select(item => KubeResourceSummaryFactory.Create(contextName, item)).ToArray();
    }

    private static async Task<IReadOnlyList<KubeResourceSummary>> QueryConfigMapsAsync(
        Kubernetes client,
        string contextName,
        string? namespaceName,
        int limit,
        CancellationToken cancellationToken)
    {
        var list = string.IsNullOrWhiteSpace(namespaceName)
            ? await client.ListConfigMapForAllNamespacesAsync(limit: limit, cancellationToken: cancellationToken)
            : await client.ListNamespacedConfigMapAsync(namespaceName, limit: limit, cancellationToken: cancellationToken);

        return list.Items.Select(item => KubeResourceSummaryFactory.Create(contextName, item)).ToArray();
    }

    private static async Task<IReadOnlyList<KubeResourceSummary>> QuerySecretsAsync(
        Kubernetes client,
        string contextName,
        string? namespaceName,
        int limit,
        CancellationToken cancellationToken)
    {
        var list = string.IsNullOrWhiteSpace(namespaceName)
            ? await client.ListSecretForAllNamespacesAsync(limit: limit, cancellationToken: cancellationToken)
            : await client.ListNamespacedSecretAsync(namespaceName, limit: limit, cancellationToken: cancellationToken);

        return list.Items.Select(item => KubeResourceSummaryFactory.Create(contextName, item)).ToArray();
    }

    private static async Task<IReadOnlyList<KubeResourceSummary>> QueryJobsAsync(
        Kubernetes client,
        string contextName,
        string? namespaceName,
        int limit,
        CancellationToken cancellationToken)
    {
        var list = string.IsNullOrWhiteSpace(namespaceName)
            ? await client.ListJobForAllNamespacesAsync(limit: limit, cancellationToken: cancellationToken)
            : await client.ListNamespacedJobAsync(namespaceName, limit: limit, cancellationToken: cancellationToken);

        return list.Items.Select(item => KubeResourceSummaryFactory.Create(contextName, item)).ToArray();
    }

    private static async Task<IReadOnlyList<KubeResourceSummary>> QueryCronJobsAsync(
        Kubernetes client,
        string contextName,
        string? namespaceName,
        int limit,
        CancellationToken cancellationToken)
    {
        var list = string.IsNullOrWhiteSpace(namespaceName)
            ? await client.ListCronJobForAllNamespacesAsync(limit: limit, cancellationToken: cancellationToken)
            : await client.ListNamespacedCronJobAsync(namespaceName, limit: limit, cancellationToken: cancellationToken);

        return list.Items.Select(item => KubeResourceSummaryFactory.Create(contextName, item)).ToArray();
    }

    private static async Task<IReadOnlyList<KubeResourceSummary>> QueryEventsAsync(
        Kubernetes client,
        string contextName,
        string? namespaceName,
        int limit,
        CancellationToken cancellationToken)
    {
        var coreOperations = (ICoreV1Operations)client;
        var list = string.IsNullOrWhiteSpace(namespaceName)
            ? await k8s.CoreV1OperationsExtensions.ListEventForAllNamespacesAsync(
                coreOperations,
                limit: limit,
                cancellationToken: cancellationToken)
            : await k8s.CoreV1OperationsExtensions.ListNamespacedEventAsync(
                coreOperations,
                namespaceName,
                limit: limit,
                cancellationToken: cancellationToken);

        return list.Items.Select(item => KubeResourceSummaryFactory.Create(contextName, item)).ToArray();
    }
}
