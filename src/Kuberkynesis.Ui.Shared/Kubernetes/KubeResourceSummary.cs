using System.Text.Json.Serialization;

namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubeResourceSummary(
    string ContextName,
    KubeResourceKind Kind,
    string ApiVersion,
    string Name,
    string? Namespace,
    string? Uid,
    string? Status,
    string? Summary,
    int? ReadyReplicas,
    int? DesiredReplicas,
    DateTimeOffset? CreatedAtUtc,
    IReadOnlyDictionary<string, string> Labels,
    KubeCustomResourceType? CustomResourceType = null)
{
    [JsonIgnore]
    public KubeResourceIdentity Identity => new(ContextName, Kind, Namespace, Name, CustomResourceType);

    [JsonIgnore]
    public string LogicalId => Identity.LogicalId;

    [JsonIgnore]
    public string SnapshotId => KubeResourceId.CreateSnapshotId(ContextName, Kind, Namespace, Name, Uid, ApiVersion, CreatedAtUtc, CustomResourceType);
}
