using System.Text.Json.Serialization;

namespace Kuberkynesis.Ui.Shared.Connection;

public enum AgentDiagnosticsIssueKind
{
    [JsonStringEnumMemberName("missing_kubeconfig")]
    MissingKubeConfig,

    [JsonStringEnumMemberName("kubeconfig_load_failed")]
    KubeConfigLoadFailed,

    [JsonStringEnumMemberName("kubectl_unavailable")]
    KubectlUnavailable,

    [JsonStringEnumMemberName("authentication_expired")]
    AuthenticationExpired,

    [JsonStringEnumMemberName("configuration_error")]
    ConfigurationError,

    [JsonStringEnumMemberName("no_queryable_contexts")]
    NoQueryableContexts
}
