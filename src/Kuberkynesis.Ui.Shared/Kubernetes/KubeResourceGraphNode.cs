using System.Text.Json.Serialization;

namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubeResourceGraphNode(
    string Id,
    string ContextName,
    KubeResourceKind? Kind,
    string ApiVersion,
    string Name,
    string? Namespace,
    string? Status,
    string? Summary,
    bool IsRoot)
{
    [JsonIgnore]
    public KubeResourceIdentity Identity => new(ContextName, Kind, Namespace, Name);

    [JsonIgnore]
    public string LogicalId => Identity.LogicalId;

    [JsonIgnore]
    public string SnapshotId => KubeResourceId.CreateSnapshotId(ContextName, Kind, Namespace, Name, uid: null, ApiVersion, createdAtUtc: null);
}
