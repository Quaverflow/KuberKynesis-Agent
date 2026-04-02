namespace Kuberkynesis.Ui.Shared.Kubernetes;

public enum KubeMetricsSourceMode
{
    MetricsApiPreferred = 0,
    PrometheusPreferred = 1,
    PrometheusOnly = 2,
    MetricsApiOnly = 3
}
