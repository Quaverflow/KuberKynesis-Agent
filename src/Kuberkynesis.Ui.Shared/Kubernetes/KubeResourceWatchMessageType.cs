using System.Text.Json.Serialization;

namespace Kuberkynesis.Ui.Shared.Kubernetes;

public enum KubeResourceWatchMessageType
{
    [JsonStringEnumMemberName("snapshot")]
    Snapshot,

    [JsonStringEnumMemberName("error")]
    Error
}
