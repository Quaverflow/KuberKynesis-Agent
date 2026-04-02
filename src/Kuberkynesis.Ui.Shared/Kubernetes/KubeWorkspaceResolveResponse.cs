namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubeWorkspaceResolveResponse(
    KubeResourceQueryRequest ResolvedQuery,
    string? CurrentContextName,
    IReadOnlyList<KubeContextSummary> ResolvedContexts,
    IReadOnlyList<KubeContextSummary> UnavailableContexts,
    IReadOnlyList<string> MissingContexts,
    IReadOnlyList<KubeResourceKind> ResolvedKinds,
    IReadOnlyList<string> Warnings,
    bool UsedCurrentContextFallback,
    bool IgnoredNamespaceFilter,
    string ScopeSummary)
{
    public IReadOnlyList<KubeClusterSummary> ResolvedClusters { get; init; } = [];
}
