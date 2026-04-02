using System.Globalization;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Kuberkynesis.LiveSurface;
using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Kube;

public sealed class KubeLiveSurfaceService(
    IKubeConfigLoader kubeConfigLoader,
    KubeResourceDetailService detailService)
{
    private const int DefaultEventLimit = 20;
    private const int MaxEventLimit = 40;
    private const int MaxTargets = 6;
    private const int MaxEventsPerTarget = 10;
    private const int WatchTimeoutSeconds = 300;
    private const string StreamName = "kuberkynesis.live.v1";
    private const string EventStreamName = "kubernetes.events";
    private const string SchemaVersion = "kubernetes.event.v1";

    public async Task<KubeLiveSurfaceQueryResponse> QueryAsync(
        KubeLiveSurfaceQueryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedRequest = NormalizeRequest(request);
        var detailRequest = CreateDetailRequest(normalizedRequest);
        var detail = await detailService.GetDetailAsync(detailRequest, cancellationToken);

        var loadResult = kubeConfigLoader.Load();
        var context = KubeResourceQueryService.ResolveTargetContexts([normalizedRequest.ContextName], loadResult).Single();

        if (context.Status is KubeContextStatus.ConfigurationError)
        {
            throw new ArgumentException(context.StatusMessage ?? $"The kube context '{context.Name}' is invalid.", nameof(request));
        }

        using var client = kubeConfigLoader.CreateClient(loadResult, context.Name);
        var targets = BuildTargets(detail)
            .Take(MaxTargets)
            .ToArray();
        var rawEventEnvelopes = new List<LiveSurfaceEnvelope>();

        foreach (var target in targets)
        {
            rawEventEnvelopes.AddRange(await ListEventsForTargetAsync(
                client,
                detail.Resource.ContextName,
                target,
                cancellationToken));
        }

        var observedAtUtc = DateTimeOffset.UtcNow;
        var envelopes = rawEventEnvelopes
            .Concat(KubeLiveSurfaceObservationFactory.CreateDerivedEnvelopes(detail.Resource, rawEventEnvelopes, observedAtUtc))
            .ToArray();
        var deduplicated = envelopes
            .GroupBy(CreateDeduplicationKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(static item => item.TimestampUtc)
            .Take(normalizedRequest.Limit)
            .ToArray();

        return new KubeLiveSurfaceQueryResponse(
            Resource: detail.Resource,
            StreamName: StreamName,
            ScopeSummary: BuildScopeSummary(targets),
            Events: deduplicated,
            Warnings: BuildWarnings(detail.Resource.ContextName, targets),
            TransparencyCommands: KubectlTransparencyFactory.CreateForLiveSurface(detailRequest));
    }

    public async Task StreamAsync(
        KubeLiveSurfaceQueryRequest request,
        Func<KubeLiveSurfaceStreamMessage, CancellationToken, Task> onMessage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(onMessage);

        var normalizedRequest = NormalizeRequest(request);
        var initialSnapshot = await QueryAsync(normalizedRequest, cancellationToken);

        await onMessage(
            new KubeLiveSurfaceStreamMessage(
                MessageType: KubeLiveSurfaceStreamMessageType.Snapshot,
                OccurredAtUtc: DateTimeOffset.UtcNow,
                Snapshot: initialSnapshot,
                ErrorMessage: null),
            cancellationToken);

        var detail = await detailService.GetDetailAsync(CreateDetailRequest(normalizedRequest), cancellationToken);
        var watchNamespace = ResolveWatchNamespace(BuildTargets(detail));
        var loadResult = kubeConfigLoader.Load();
        var context = KubeResourceQueryService.ResolveTargetContexts([normalizedRequest.ContextName], loadResult).Single();

        if (context.Status is KubeContextStatus.ConfigurationError)
        {
            throw new ArgumentException(context.StatusMessage ?? $"The kube context '{context.Name}' is invalid.", nameof(request));
        }

        using var client = kubeConfigLoader.CreateClient(loadResult, context.Name);
        var signals = KubeResourceWatchService.CreateSignalChannel();
        var pendingEventCount = 0;
        Exception? watchFailure = null;

        using var watcher = CreateEventWatcher(
            client,
            watchNamespace,
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
            var coalescedEventCount = KubeResourceWatchService.DrainPendingSignals(signals.Reader, ref pendingEventCount);

            if (coalescedEventCount is 0)
            {
                continue;
            }

            var snapshot = await QueryAsync(normalizedRequest, cancellationToken);

            await onMessage(
                new KubeLiveSurfaceStreamMessage(
                    MessageType: KubeLiveSurfaceStreamMessageType.Snapshot,
                    OccurredAtUtc: DateTimeOffset.UtcNow,
                    Snapshot: snapshot,
                    ErrorMessage: null,
                    CoalescedEventCount: coalescedEventCount),
                cancellationToken);
        }

        if (watchFailure is not null)
        {
            await onMessage(
                new KubeLiveSurfaceStreamMessage(
                    MessageType: KubeLiveSurfaceStreamMessageType.Error,
                    OccurredAtUtc: DateTimeOffset.UtcNow,
                    Snapshot: null,
                    ErrorMessage: watchFailure.Message),
                cancellationToken);

            throw watchFailure;
        }
    }

    private static KubeLiveSurfaceQueryRequest NormalizeRequest(KubeLiveSurfaceQueryRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ContextName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);

        var limit = request.Limit switch
        {
            <= 0 => DefaultEventLimit,
            > MaxEventLimit => MaxEventLimit,
            _ => request.Limit
        };

        return request with
        {
            ContextName = request.ContextName.Trim(),
            Namespace = string.IsNullOrWhiteSpace(request.Namespace) ? null : request.Namespace.Trim(),
            Name = request.Name.Trim(),
            Limit = limit
        };
    }

    private static KubeResourceDetailRequest CreateDetailRequest(KubeLiveSurfaceQueryRequest request)
    {
        return new KubeResourceDetailRequest
        {
            ContextName = request.ContextName,
            Kind = request.Kind,
            Namespace = request.Namespace,
            Name = request.Name
        };
    }

    private static IReadOnlyList<LiveSurfaceTarget> BuildTargets(KubeResourceDetailResponse detail)
    {
        var targets = new List<LiveSurfaceTarget>
        {
            new(
                Relationship: "Selected resource",
                Kind: detail.Resource.Kind,
                Name: detail.Resource.Name,
                Namespace: detail.Resource.Namespace)
        };

        foreach (var relatedResource in detail.RelatedResources)
        {
            if (relatedResource.Kind is null)
            {
                continue;
            }

            if (IsNamespacedKind(relatedResource.Kind.Value) && string.IsNullOrWhiteSpace(relatedResource.Namespace))
            {
                continue;
            }

            targets.Add(new LiveSurfaceTarget(
                Relationship: relatedResource.Relationship,
                Kind: relatedResource.Kind.Value,
                Name: relatedResource.Name,
                Namespace: relatedResource.Namespace));
        }

        return targets
            .Distinct()
            .ToArray();
    }

    private static async Task<IReadOnlyList<LiveSurfaceEnvelope>> ListEventsForTargetAsync(
        Kubernetes client,
        string contextName,
        LiveSurfaceTarget target,
        CancellationToken cancellationToken)
    {
        var fieldSelector = $"involvedObject.kind={GetKubernetesKindName(target.Kind)},involvedObject.name={target.Name}";
        Corev1EventList list;

        if (IsNamespacedKind(target.Kind))
        {
            list = await k8s.CoreV1OperationsExtensions.ListNamespacedEventAsync(
                client,
                target.Namespace!,
                fieldSelector: fieldSelector,
                limit: MaxEventsPerTarget,
                cancellationToken: cancellationToken);
        }
        else
        {
            list = await k8s.CoreV1OperationsExtensions.ListEventForAllNamespacesAsync(
                client,
                fieldSelector: fieldSelector,
                limit: MaxEventsPerTarget,
                cancellationToken: cancellationToken);
        }

        return list.Items
            .Select(item => CreateEnvelope(contextName, target, item))
            .ToArray();
    }

    private static LiveSurfaceEnvelope CreateEnvelope(
        string contextName,
        LiveSurfaceTarget target,
        Corev1Event item)
    {
        var tags = CreateTags(contextName, target, item);
        var fields = CreateFields(target, item);
        var summary = string.IsNullOrWhiteSpace(item.Message)
            ? item.Reason ?? "Kubernetes event"
            : item.Message.Trim();

        return new LiveSurfaceEnvelope(
            SchemaVersion: SchemaVersion,
            Stream: EventStreamName,
            EventType: string.IsNullOrWhiteSpace(item.Reason) ? item.Type ?? "Observed" : item.Reason,
            TimestampUtc: ResolveOccurredAtUtc(item),
            Severity: ResolveSeverity(item.Type),
            Summary: summary,
            Namespace: item.Metadata?.NamespaceProperty ?? target.Namespace,
            ResourceKind: target.Kind.ToString(),
            ResourceName: target.Name,
            Component: item.Source?.Component,
            Tags: tags,
            Fields: fields,
            Category: "event");
    }

    private static IReadOnlyDictionary<string, string> CreateTags(
        string contextName,
        LiveSurfaceTarget target,
        Corev1Event item)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["context"] = contextName,
            ["category"] = "event",
            ["relationship"] = target.Relationship,
            ["resourceKind"] = target.Kind.ToString(),
            ["resourceName"] = target.Name
        };

        if (!string.IsNullOrWhiteSpace(target.Namespace))
        {
            tags["namespace"] = target.Namespace;
        }

        if (!string.IsNullOrWhiteSpace(item.Type))
        {
            tags["type"] = item.Type;
        }

        if (!string.IsNullOrWhiteSpace(item.Reason))
        {
            tags["reason"] = item.Reason;
        }

        if (!string.IsNullOrWhiteSpace(item.Source?.Component))
        {
            tags["component"] = item.Source.Component;
        }

        return tags;
    }

    private static IReadOnlyDictionary<string, string> CreateFields(
        LiveSurfaceTarget target,
        Corev1Event item)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["relationship"] = target.Relationship,
            ["resourceKind"] = target.Kind.ToString(),
            ["resourceName"] = target.Name,
            ["count"] = item.Count?.ToString(CultureInfo.InvariantCulture) ?? "1"
        };

        AddField(fields, "namespace", item.Metadata?.NamespaceProperty ?? target.Namespace);
        AddField(fields, "type", item.Type);
        AddField(fields, "reason", item.Reason);
        AddField(fields, "message", item.Message);
        AddField(fields, "action", item.Action);
        AddField(fields, "component", item.Source?.Component);
        AddField(fields, "host", item.Source?.Host);
        AddField(fields, "involvedKind", item.InvolvedObject?.Kind);
        AddField(fields, "involvedName", item.InvolvedObject?.Name);
        AddField(fields, "relatedKind", item.Related?.Kind);
        AddField(fields, "relatedName", item.Related?.Name);

        return fields;
    }

    private static void AddField(IDictionary<string, string> fields, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            fields[key] = value.Trim();
        }
    }

    private static IReadOnlyList<KubeQueryWarning> BuildWarnings(string contextName, IReadOnlyList<LiveSurfaceTarget> targets)
    {
        var warnings = new List<KubeQueryWarning>
        {
            new(
                ContextName: contextName,
                Message: "Live Surface v1 now blends Kubernetes Event envelopes with derived status, activity, and likely-cause observations for the selected scope. App-defined business-event producers are still future work.")
        };

        if (targets.Count >= MaxTargets)
        {
            warnings.Add(new KubeQueryWarning(
                ContextName: contextName,
                Message: $"This Live Surface view is capped to {MaxTargets} modeled targets to keep the stream responsive."));
        }

        return warnings;
    }

    private static string BuildScopeSummary(IReadOnlyList<LiveSurfaceTarget> targets)
    {
        return targets.Count switch
        {
            0 => "Streaming Kubernetes events plus derived status and causality observations for the selected resource scope.",
            1 => "Streaming Kubernetes events plus derived status and causality observations for the selected resource.",
            _ => $"Streaming Kubernetes events plus derived status and causality observations for the selected resource and {targets.Count - 1} nearby modeled relation(s)."
        };
    }

    private static string CreateDeduplicationKey(LiveSurfaceEnvelope envelope)
    {
        envelope.Fields.TryGetValue("relationship", out var relationship);
        envelope.Fields.TryGetValue("reason", out var reason);
        envelope.Fields.TryGetValue("message", out var message);

        return string.Join(
            "|",
            envelope.Stream ?? string.Empty,
            envelope.Category ?? string.Empty,
            relationship ?? string.Empty,
            envelope.ResourceKind ?? string.Empty,
            envelope.Namespace ?? string.Empty,
            envelope.ResourceName ?? string.Empty,
            envelope.TimestampUtc.ToString("O", CultureInfo.InvariantCulture),
            reason ?? envelope.EventType,
            message ?? envelope.Summary ?? string.Empty);
    }

    private static string? ResolveWatchNamespace(IReadOnlyList<LiveSurfaceTarget> targets)
    {
        if (targets.Count is 0)
        {
            return null;
        }

        if (targets.Any(static target => !IsNamespacedKind(target.Kind) || string.IsNullOrWhiteSpace(target.Namespace)))
        {
            return null;
        }

        var namespaces = targets
            .Select(static target => target.Namespace!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return namespaces.Length is 1
            ? namespaces[0]
            : null;
    }

    private static IDisposable CreateEventWatcher(
        Kubernetes client,
        string? namespaceName,
        Action onEvent,
        Action<Exception> onError,
        Action onClosed,
        CancellationToken cancellationToken)
    {
        var coreOperations = (ICoreV1Operations)client;

        return string.IsNullOrWhiteSpace(namespaceName)
            ? coreOperations.ListEventForAllNamespacesWithHttpMessagesAsync(
                watch: true,
                timeoutSeconds: WatchTimeoutSeconds,
                cancellationToken: cancellationToken).Watch<Corev1Event, Corev1EventList>((_, _) => onEvent(), onError, onClosed)
            : coreOperations.ListNamespacedEventWithHttpMessagesAsync(
                namespaceName.Trim(),
                watch: true,
                timeoutSeconds: WatchTimeoutSeconds,
                cancellationToken: cancellationToken).Watch<Corev1Event, Corev1EventList>((_, _) => onEvent(), onError, onClosed);
    }

    private static DateTimeOffset ResolveOccurredAtUtc(Corev1Event item)
    {
        if (item.EventTime != default)
        {
            return new DateTimeOffset(DateTime.SpecifyKind(item.EventTime!.Value, DateTimeKind.Utc));
        }

        if (item.LastTimestamp is not null)
        {
            return item.LastTimestamp.Value;
        }

        if (item.FirstTimestamp is not null)
        {
            return item.FirstTimestamp.Value;
        }

        return item.Metadata?.CreationTimestamp ?? DateTimeOffset.UtcNow;
    }

    private static string ResolveSeverity(string? type)
    {
        return string.Equals(type, "Warning", StringComparison.OrdinalIgnoreCase)
            ? "warning"
            : "normal";
    }

    private static bool IsNamespacedKind(KubeResourceKind kind)
    {
        return kind is not KubeResourceKind.Namespace and not KubeResourceKind.Node;
    }

    private static string GetKubernetesKindName(KubeResourceKind kind)
    {
        return kind switch
        {
            KubeResourceKind.Namespace => "Namespace",
            KubeResourceKind.Node => "Node",
            KubeResourceKind.Pod => "Pod",
            KubeResourceKind.Deployment => "Deployment",
            KubeResourceKind.ReplicaSet => "ReplicaSet",
            KubeResourceKind.StatefulSet => "StatefulSet",
            KubeResourceKind.DaemonSet => "DaemonSet",
            KubeResourceKind.Service => "Service",
            KubeResourceKind.Ingress => "Ingress",
            KubeResourceKind.ConfigMap => "ConfigMap",
            KubeResourceKind.Secret => "Secret",
            KubeResourceKind.Job => "Job",
            KubeResourceKind.CronJob => "CronJob",
            KubeResourceKind.Event => "Event",
            _ => kind.ToString()
        };
    }

    private sealed record LiveSurfaceTarget(
        string Relationship,
        KubeResourceKind Kind,
        string Name,
        string? Namespace);
}
