using System.Text.Json.Serialization;

namespace Kuberkynesis.Ui.Shared.Kubernetes;

public enum KubeActionExecutionStreamMessageType
{
    [JsonStringEnumMemberName("snapshot")]
    Snapshot,

    [JsonStringEnumMemberName("completed")]
    Completed,

    [JsonStringEnumMemberName("cancelled")]
    Cancelled,

    [JsonStringEnumMemberName("error")]
    Error
}
