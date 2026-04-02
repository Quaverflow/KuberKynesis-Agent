namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubeResourceTimelineResponse(
    KubeResourceSummary Resource,
    IReadOnlyList<KubeResourceTimelineEvent> Events,
    IReadOnlyList<string> LikelyCauses,
    IReadOnlyList<KubeQueryWarning> Warnings,
    IReadOnlyList<KubectlCommandPreview>? TransparencyCommands = null);
