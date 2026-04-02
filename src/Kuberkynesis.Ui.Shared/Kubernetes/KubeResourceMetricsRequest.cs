namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubeResourceMetricsRequest(
    string ContextName,
    KubeResourceKind Kind,
    string? Namespace,
    string Name);
