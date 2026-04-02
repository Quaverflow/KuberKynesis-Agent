namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubeResourceMetricsResponse(
    KubeResourceKind Kind,
    string ContextName,
    string Name,
    string? Namespace,
    bool MetricsAvailable,
    KubeMetricsSourceKind MetricsSource,
    string? MetricsStatusMessage,
    long? CpuMillicores,
    long? MemoryBytes,
    int ContributorCount,
    int UnhealthyContributorCount,
    int RestartCount,
    string? SchedulingPressure,
    DateTimeOffset? CollectedAtUtc,
    string? Window,
    IReadOnlyList<KubeResourceMetricsContributor> Contributors,
    IReadOnlyList<KubeQueryWarning> Warnings,
    IReadOnlyList<KubectlCommandPreview> TransparencyCommands);
