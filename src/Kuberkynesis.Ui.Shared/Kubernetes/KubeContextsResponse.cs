namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubeContextsResponse(
    string? CurrentContextName,
    IReadOnlyList<string> SourcePaths,
    IReadOnlyList<KubeContextSummary> Contexts,
    IReadOnlyList<string> Warnings)
{
    public IReadOnlyList<KubeClusterSummary> Clusters { get; init; } = [];
}
