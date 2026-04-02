namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubeResourceMetricsContributor(
    string Name,
    string? Namespace,
    string? Status,
    bool Healthy,
    int RestartCount,
    long? CpuMillicores,
    long? MemoryBytes);
