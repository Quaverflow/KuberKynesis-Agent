namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubeClusterSummary(
    string Name,
    string? Server,
    bool ContainsCurrentContext,
    int ContextCount,
    int QueryableContextCount,
    IReadOnlyList<string> ContextNames);
