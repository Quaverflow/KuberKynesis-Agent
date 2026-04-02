using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Kube;

internal static class KubeResourceTimelineFactory
{
    public static KubeResourceTimelineResponse Create(
        KubeResourceSummary resource,
        IReadOnlyList<KubeResourceTimelineEvent> events,
        IReadOnlyList<KubectlCommandPreview> transparencyCommands)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var orderedEvents = events
            .OrderByDescending(item => item.OccurredAtUtc)
            .ThenByDescending(item => item.Count ?? 1)
            .ToArray();

        return new KubeResourceTimelineResponse(
            Resource: resource,
            Events: orderedEvents,
            LikelyCauses: KubeResourceCauseInference.InferLikelyCauses(orderedEvents)
                .Select(static cause => cause.Summary)
                .ToArray(),
            Warnings: [],
            TransparencyCommands: transparencyCommands);
    }
}
