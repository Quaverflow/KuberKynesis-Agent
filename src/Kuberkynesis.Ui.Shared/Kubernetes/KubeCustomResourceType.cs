using System.Text.Json.Serialization;

namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubeCustomResourceType(
    string Group,
    string Version,
    string Kind,
    string Plural,
    bool Namespaced,
    string? Singular = null,
    string? ListKind = null)
{
    [JsonIgnore]
    public string ApiVersion => $"{Group}/{Version}";

    [JsonIgnore]
    public string DefinitionId => $"{Group}/{Version}/{Plural}";

    [JsonIgnore]
    public string DisplayName => $"{Kind} ({Plural}.{Group}/{Version})";

    [JsonIgnore]
    public string ScopeLabel => Namespaced ? "Namespaced" : "Cluster";
}
