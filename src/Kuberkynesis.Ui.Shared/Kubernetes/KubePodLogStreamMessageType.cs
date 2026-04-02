using System.Text.Json.Serialization;

namespace Kuberkynesis.Ui.Shared.Kubernetes;

public enum KubePodLogStreamMessageType
{
    [JsonStringEnumMemberName("snapshot")]
    Snapshot,

    [JsonStringEnumMemberName("append")]
    Append,

    [JsonStringEnumMemberName("error")]
    Error
}
