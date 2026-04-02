using System.Text.Json.Serialization;

namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubeResourceIdentity(
    string ContextName,
    KubeResourceKind? Kind,
    string? Namespace,
    string Name,
    KubeCustomResourceType? CustomResourceType = null)
{
    public const string ClusterScopedNamespace = KubeResourceId.ClusterScopedNamespace;
    public const string UnknownKind = KubeResourceId.UnknownKind;

    [JsonIgnore]
    public string LogicalId => KubeResourceId.CreateLogicalId(ContextName, Kind, Namespace, Name, CustomResourceType);

    [JsonIgnore]
    public string SnapshotId => KubeResourceId.CreateSnapshotId(ContextName, Kind, Namespace, Name, uid: null, apiVersion: null, createdAtUtc: null, customResourceType: CustomResourceType);

    public string ToKey()
    {
        return LogicalId;
    }

    public static string Create(
        string contextName,
        KubeResourceKind? kind,
        string? namespaceName,
        string name,
        KubeCustomResourceType? customResourceType = null)
    {
        return KubeResourceId.CreateLogicalId(contextName, kind, namespaceName, name, customResourceType);
    }

    public static bool TryParse(string? value, out KubeResourceIdentity? identity)
    {
        return KubeResourceId.TryParseLogicalId(value, out identity);
    }
}
