namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubePodMetricsSample(
    string ContextName,
    string Namespace,
    string PodName,
    int RestartCount,
    long? CpuMillicores,
    long? MemoryBytes,
    DateTimeOffset? CollectedAtUtc,
    string? Window);
