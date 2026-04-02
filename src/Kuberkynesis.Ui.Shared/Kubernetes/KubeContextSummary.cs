namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubeContextSummary(
    string Name,
    bool IsCurrent,
    string? ClusterName,
    string? Namespace,
    string? UserName,
    string? Server,
    KubeContextStatus Status,
    string? StatusMessage);
