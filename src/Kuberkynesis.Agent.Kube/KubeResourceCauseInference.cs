using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Kube;

internal static class KubeResourceCauseInference
{
    public static IReadOnlyList<KubeResourceLikelyCause> InferLikelyCauses(IReadOnlyList<KubeResourceTimelineEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        var likelyCauses = new List<KubeResourceLikelyCause>();

        AddLikelyCauseIfNeeded(
            likelyCauses,
            events,
            static item => ContainsAny(item.Reason, "FailedScheduling") || ContainsAny(item.Message, "didn't match node selector", "Insufficient"),
            static item => new KubeResourceLikelyCause(
                EventType: "Scheduling pressure",
                Summary: $"Likely scheduling pressure or placement mismatch. Evidence: {item.Reason} on {item.SourceName}.",
                Severity: "warning",
                OccurredAtUtc: item.OccurredAtUtc,
                SourceRelationship: item.SourceRelationship,
                SourceKind: item.SourceKind,
                SourceName: item.SourceName,
                SourceNamespace: item.SourceNamespace,
                EvidenceReason: item.Reason));

        AddLikelyCauseIfNeeded(
            likelyCauses,
            events,
            static item => ContainsAny(item.Reason, "Unhealthy") || ContainsAny(item.Message, "readiness probe failed", "liveness probe failed"),
            static item => new KubeResourceLikelyCause(
                EventType: "Health issue",
                Summary: $"Likely probe health issue. Evidence: {item.Reason} on {item.SourceName}.",
                Severity: "warning",
                OccurredAtUtc: item.OccurredAtUtc,
                SourceRelationship: item.SourceRelationship,
                SourceKind: item.SourceKind,
                SourceName: item.SourceName,
                SourceNamespace: item.SourceNamespace,
                EvidenceReason: item.Reason));

        AddLikelyCauseIfNeeded(
            likelyCauses,
            events,
            static item => ContainsAny(item.Reason, "BackOff", "CrashLoopBackOff") || ContainsAny(item.Message, "back-off restarting failed container"),
            static item => new KubeResourceLikelyCause(
                EventType: "Restart loop",
                Summary: $"Likely container restart loop. Evidence: {item.Reason} on {item.SourceName}.",
                Severity: "warning",
                OccurredAtUtc: item.OccurredAtUtc,
                SourceRelationship: item.SourceRelationship,
                SourceKind: item.SourceKind,
                SourceName: item.SourceName,
                SourceNamespace: item.SourceNamespace,
                EvidenceReason: item.Reason));

        AddLikelyCauseIfNeeded(
            likelyCauses,
            events,
            static item => ContainsAny(item.Reason, "FailedMount", "FailedAttachVolume") || ContainsAny(item.Message, "MountVolume", "AttachVolume"),
            static item => new KubeResourceLikelyCause(
                EventType: "Dependency issue",
                Summary: $"Likely config or volume dependency issue. Evidence: {item.Reason} on {item.SourceName}.",
                Severity: "warning",
                OccurredAtUtc: item.OccurredAtUtc,
                SourceRelationship: item.SourceRelationship,
                SourceKind: item.SourceKind,
                SourceName: item.SourceName,
                SourceNamespace: item.SourceNamespace,
                EvidenceReason: item.Reason));

        AddLikelyCauseIfNeeded(
            likelyCauses,
            events,
            static item => item.IsRootResource && ContainsAny(item.Reason, "ScalingReplicaSet", "SuccessfulCreate", "SuccessfulDelete"),
            static item => new KubeResourceLikelyCause(
                EventType: "Rollout activity",
                Summary: $"Likely rollout activity on the selected resource. Evidence: {item.Reason}.",
                Severity: "normal",
                OccurredAtUtc: item.OccurredAtUtc,
                SourceRelationship: item.SourceRelationship,
                SourceKind: item.SourceKind,
                SourceName: item.SourceName,
                SourceNamespace: item.SourceNamespace,
                EvidenceReason: item.Reason));

        return likelyCauses;
    }

    private static void AddLikelyCauseIfNeeded(
        ICollection<KubeResourceLikelyCause> likelyCauses,
        IEnumerable<KubeResourceTimelineEvent> events,
        Func<KubeResourceTimelineEvent, bool> predicate,
        Func<KubeResourceTimelineEvent, KubeResourceLikelyCause> formatter)
    {
        var match = events.FirstOrDefault(predicate);

        if (match is not null)
        {
            likelyCauses.Add(formatter(match));
        }
    }

    private static bool ContainsAny(string? value, params string[] patterns)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return patterns.Any(pattern => value.Contains(pattern, StringComparison.OrdinalIgnoreCase));
    }
}

internal sealed record KubeResourceLikelyCause(
    string EventType,
    string Summary,
    string Severity,
    DateTimeOffset OccurredAtUtc,
    string SourceRelationship,
    KubeResourceKind? SourceKind,
    string SourceName,
    string? SourceNamespace,
    string EvidenceReason);
