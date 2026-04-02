namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubePodMetricsQueryRequest(
    IReadOnlyList<KubeResourceIdentity> Targets);
