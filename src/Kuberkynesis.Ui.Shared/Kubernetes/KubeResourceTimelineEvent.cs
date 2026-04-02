namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubeResourceTimelineEvent(
    DateTimeOffset OccurredAtUtc,
    string SourceRelationship,
    KubeResourceKind? SourceKind,
    string SourceName,
    string? SourceNamespace,
    string Type,
    string Reason,
    string Message,
    int? Count,
    bool IsRootResource);
