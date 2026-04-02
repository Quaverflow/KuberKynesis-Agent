namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubePodMetricsQueryResponse(
    bool MetricsAvailable,
    KubeMetricsSourceKind MetricsSource,
    string? MetricsStatusMessage,
    DateTimeOffset? CollectedAtUtc,
    string? Window,
    IReadOnlyList<KubePodMetricsSample> Pods,
    IReadOnlyList<KubeQueryWarning> Warnings,
    IReadOnlyList<KubectlCommandPreview> TransparencyCommands);
