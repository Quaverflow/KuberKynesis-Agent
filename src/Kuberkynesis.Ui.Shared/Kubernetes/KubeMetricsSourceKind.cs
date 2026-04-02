namespace Kuberkynesis.Ui.Shared.Kubernetes;

public enum KubeMetricsSourceKind
{
    Unavailable = 0,
    KubernetesMetricsApi = 1,
    Prometheus = 2,
    Mixed = 3
}
