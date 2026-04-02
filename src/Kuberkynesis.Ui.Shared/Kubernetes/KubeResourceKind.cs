using System.Text.Json.Serialization;

namespace Kuberkynesis.Ui.Shared.Kubernetes;

public enum KubeResourceKind
{
    [JsonStringEnumMemberName("customresource")]
    CustomResource,

    [JsonStringEnumMemberName("namespace")]
    Namespace,

    [JsonStringEnumMemberName("node")]
    Node,

    [JsonStringEnumMemberName("pod")]
    Pod,

    [JsonStringEnumMemberName("deployment")]
    Deployment,

    [JsonStringEnumMemberName("replicaset")]
    ReplicaSet,

    [JsonStringEnumMemberName("statefulset")]
    StatefulSet,

    [JsonStringEnumMemberName("daemonset")]
    DaemonSet,

    [JsonStringEnumMemberName("service")]
    Service,

    [JsonStringEnumMemberName("ingress")]
    Ingress,

    [JsonStringEnumMemberName("configmap")]
    ConfigMap,

    [JsonStringEnumMemberName("secret")]
    Secret,

    [JsonStringEnumMemberName("job")]
    Job,

    [JsonStringEnumMemberName("cronjob")]
    CronJob,

    [JsonStringEnumMemberName("event")]
    Event
}
