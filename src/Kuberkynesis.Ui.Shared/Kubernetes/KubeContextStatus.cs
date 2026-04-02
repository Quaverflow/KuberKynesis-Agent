using System.Text.Json.Serialization;

namespace Kuberkynesis.Ui.Shared.Kubernetes;

public enum KubeContextStatus
{
    [JsonStringEnumMemberName("configured")]
    Configured,

    [JsonStringEnumMemberName("authentication_expired")]
    AuthenticationExpired,

    [JsonStringEnumMemberName("configuration_error")]
    ConfigurationError
}
