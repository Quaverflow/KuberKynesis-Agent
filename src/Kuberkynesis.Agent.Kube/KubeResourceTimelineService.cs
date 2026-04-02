using k8s;
using k8s.Models;
using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Kube;

public sealed class KubeResourceTimelineService(
    IKubeConfigLoader kubeConfigLoader,
    KubeResourceDetailService detailService)
{
    private const int MaxTimelineTargets = 6;
    private const int MaxEventsPerTarget = 8;
    private const int MaxTimelineEvents = 30;

    public async Task<KubeResourceTimelineResponse> GetTimelineAsync(
        KubeResourceTimelineRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var detailRequest = new KubeResourceDetailRequest
        {
            ContextName = request.ContextName,
            Kind = request.Kind,
            Namespace = request.Namespace,
            Name = request.Name
        };

        var detail = await detailService.GetDetailAsync(detailRequest, cancellationToken);
        var loadResult = kubeConfigLoader.Load();
        var context = KubeResourceQueryService.ResolveTargetContexts([request.ContextName], loadResult).Single();

        if (context.Status is KubeContextStatus.ConfigurationError)
        {
            throw new ArgumentException(context.StatusMessage ?? $"The kube context '{context.Name}' is invalid.", nameof(request));
        }

        using var client = kubeConfigLoader.CreateClient(loadResult, context.Name);

        var timelineTargets = BuildTimelineTargets(detail)
            .Take(MaxTimelineTargets)
            .ToArray();
        var events = new List<KubeResourceTimelineEvent>();

        foreach (var target in timelineTargets)
        {
            var targetEvents = await ListEventsForTargetAsync(client, target, cancellationToken);
            events.AddRange(targetEvents);
        }

        var deduplicatedEvents = events
            .GroupBy(item => $"{item.SourceRelationship}|{item.SourceKind}|{item.SourceNamespace}|{item.SourceName}|{item.OccurredAtUtc:O}|{item.Reason}|{item.Message}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(item => item.OccurredAtUtc)
            .Take(MaxTimelineEvents)
            .ToArray();

        return KubeResourceTimelineFactory.Create(
            detail.Resource,
            deduplicatedEvents,
            KubectlTransparencyFactory.CreateForTimeline(detailRequest));
    }

    private static IEnumerable<TimelineTarget> BuildTimelineTargets(KubeResourceDetailResponse detail)
    {
        yield return new TimelineTarget(
            Relationship: "Selected resource",
            Kind: detail.Resource.Kind,
            Name: detail.Resource.Name,
            Namespace: detail.Resource.Namespace,
            IsRootResource: true);

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

            yield return new TimelineTarget(
                Relationship: relatedResource.Relationship,
                Kind: relatedResource.Kind.Value,
                Name: relatedResource.Name,
                Namespace: relatedResource.Namespace,
                IsRootResource: false);
        }
    }

    private static async Task<IReadOnlyList<KubeResourceTimelineEvent>> ListEventsForTargetAsync(
        Kubernetes client,
        TimelineTarget target,
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
            .Select(item => new KubeResourceTimelineEvent(
                OccurredAtUtc: ResolveOccurredAtUtc(item),
                SourceRelationship: target.Relationship,
                SourceKind: target.Kind,
                SourceName: target.Name,
                SourceNamespace: target.Namespace,
                Type: item.Type ?? "Normal",
                Reason: item.Reason ?? "Unknown",
                Message: item.Message ?? "No event message was recorded.",
                Count: item.Count,
                IsRootResource: target.IsRootResource))
            .ToArray();
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

    private sealed record TimelineTarget(
        string Relationship,
        KubeResourceKind Kind,
        string Name,
        string? Namespace,
        bool IsRootResource);
}
