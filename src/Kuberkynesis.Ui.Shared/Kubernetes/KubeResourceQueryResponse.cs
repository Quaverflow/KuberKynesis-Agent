namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubeResourceQueryResponse(
    KubeResourceKind Kind,
    IReadOnlyList<string> Contexts,
    int LimitApplied,
    IReadOnlyList<KubeResourceSummary> Resources,
    IReadOnlyList<KubeQueryWarning> Warnings,
    IReadOnlyList<KubectlCommandPreview>? TransparencyCommands = null)
{
    public KubeResourceQueryPerformance? Performance { get; init; }
}
