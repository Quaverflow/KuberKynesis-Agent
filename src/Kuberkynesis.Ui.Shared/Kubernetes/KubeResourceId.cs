namespace Kuberkynesis.Ui.Shared.Kubernetes;

public static class KubeResourceId
{
    private const string LogicalPrefix = "kr";
    private const string SnapshotPrefix = "ks";

    public const string ClusterScopedNamespace = "cluster-scoped";
    public const string UnknownKind = "Unknown";

    public static string CreateLogicalId(
        string contextName,
        KubeResourceKind? kind,
        string? namespaceName,
        string name,
        KubeCustomResourceType? customResourceType = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contextName);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var kindValue = kind?.ToString() ?? UnknownKind;
        var namespaceValue = string.IsNullOrWhiteSpace(namespaceName) ? ClusterScopedNamespace : namespaceName.Trim();

        var segments = new List<string>
        {
            LogicalPrefix,
            Uri.EscapeDataString(contextName.Trim()),
            Uri.EscapeDataString(kindValue),
            Uri.EscapeDataString(namespaceValue),
            Uri.EscapeDataString(name.Trim())
        };

        if (customResourceType is not null)
        {
            segments.Add(Uri.EscapeDataString(CreateCustomResourceTypeToken(customResourceType)));
        }

        return string.Join("|", segments);
    }

    public static string CreateSnapshotId(
        string contextName,
        KubeResourceKind? kind,
        string? namespaceName,
        string name,
        string? uid,
        string? apiVersion,
        DateTimeOffset? createdAtUtc,
        KubeCustomResourceType? customResourceType = null)
    {
        if (!string.IsNullOrWhiteSpace(uid))
        {
            return string.Join("|",
                SnapshotPrefix,
                "uid",
                Uri.EscapeDataString(uid.Trim()));
        }

        return string.Join("|",
            SnapshotPrefix,
            "synthetic",
            CreateLogicalId(contextName, kind, namespaceName, name, customResourceType),
            Uri.EscapeDataString(string.IsNullOrWhiteSpace(apiVersion) ? "unknown" : apiVersion.Trim()),
            createdAtUtc?.ToUnixTimeMilliseconds().ToString() ?? "none");
    }

    public static bool TryParseLogicalId(string? value, out KubeResourceIdentity? identity)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            identity = null;
            return false;
        }

        if (value.StartsWith($"{LogicalPrefix}|", StringComparison.Ordinal))
        {
            var segments = value.Split('|', 6, StringSplitOptions.None);

            if (segments.Length is < 5 or > 6)
            {
                identity = null;
                return false;
            }

            var contextName = Uri.UnescapeDataString(segments[1]);
            var kindValue = Uri.UnescapeDataString(segments[2]);
            var namespaceValue = Uri.UnescapeDataString(segments[3]);
            var name = Uri.UnescapeDataString(segments[4]);

            if (string.IsNullOrWhiteSpace(contextName) ||
                string.IsNullOrWhiteSpace(kindValue) ||
                string.IsNullOrWhiteSpace(namespaceValue) ||
                string.IsNullOrWhiteSpace(name))
            {
                identity = null;
                return false;
            }

            identity = new KubeResourceIdentity(
                ContextName: contextName,
                Kind: ParseKind(kindValue),
                Namespace: string.Equals(namespaceValue, ClusterScopedNamespace, StringComparison.Ordinal) ? null : namespaceValue,
                Name: name,
                CustomResourceType: segments.Length is 6
                    ? ParseCustomResourceTypeToken(Uri.UnescapeDataString(segments[5]))
                    : null);
            return true;
        }

        return TryParseLegacyLogicalId(value, out identity);
    }

    private static bool TryParseLegacyLogicalId(string value, out KubeResourceIdentity? identity)
    {
        var segments = value.Split('|', 4, StringSplitOptions.None);

        if (segments.Length is not 4 ||
            string.IsNullOrWhiteSpace(segments[0]) ||
            string.IsNullOrWhiteSpace(segments[1]) ||
            string.IsNullOrWhiteSpace(segments[2]) ||
            string.IsNullOrWhiteSpace(segments[3]))
        {
            identity = null;
            return false;
        }

        identity = new KubeResourceIdentity(
            ContextName: segments[0],
            Kind: ParseKind(segments[1]),
            Namespace: string.Equals(segments[2], ClusterScopedNamespace, StringComparison.Ordinal) || string.Equals(segments[2], "_cluster", StringComparison.Ordinal)
                ? null
                : segments[2],
            Name: segments[3]);
        return true;
    }

    private static KubeResourceKind? ParseKind(string value)
    {
        if (string.Equals(value, UnknownKind, StringComparison.Ordinal))
        {
            return null;
        }

        if (Enum.TryParse<KubeResourceKind>(value, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return value.ToLowerInvariant() switch
        {
            "customresource" => KubeResourceKind.CustomResource,
            "namespace" => KubeResourceKind.Namespace,
            "node" => KubeResourceKind.Node,
            "pod" => KubeResourceKind.Pod,
            "deployment" => KubeResourceKind.Deployment,
            "replicaset" => KubeResourceKind.ReplicaSet,
            "statefulset" => KubeResourceKind.StatefulSet,
            "daemonset" => KubeResourceKind.DaemonSet,
            "service" => KubeResourceKind.Service,
            "ingress" => KubeResourceKind.Ingress,
            "configmap" => KubeResourceKind.ConfigMap,
            "secret" => KubeResourceKind.Secret,
            "job" => KubeResourceKind.Job,
            "cronjob" => KubeResourceKind.CronJob,
            "event" => KubeResourceKind.Event,
            _ => null
        };
    }

    private static string CreateCustomResourceTypeToken(KubeCustomResourceType customResourceType)
    {
        return string.Join("~",
            Uri.EscapeDataString(customResourceType.Group),
            Uri.EscapeDataString(customResourceType.Version),
            Uri.EscapeDataString(customResourceType.Kind),
            Uri.EscapeDataString(customResourceType.Plural),
            customResourceType.Namespaced ? "ns" : "cluster",
            Uri.EscapeDataString(customResourceType.Singular ?? string.Empty),
            Uri.EscapeDataString(customResourceType.ListKind ?? string.Empty));
    }

    private static KubeCustomResourceType? ParseCustomResourceTypeToken(string value)
    {
        var segments = value.Split('~', StringSplitOptions.None);

        if (segments.Length is < 5)
        {
            return null;
        }

        return new KubeCustomResourceType(
            Group: Uri.UnescapeDataString(segments[0]),
            Version: Uri.UnescapeDataString(segments[1]),
            Kind: Uri.UnescapeDataString(segments[2]),
            Plural: Uri.UnescapeDataString(segments[3]),
            Namespaced: !string.Equals(segments[4], "cluster", StringComparison.Ordinal),
            Singular: segments.Length > 5 ? NullIfWhiteSpace(Uri.UnescapeDataString(segments[5])) : null,
            ListKind: segments.Length > 6 ? NullIfWhiteSpace(Uri.UnescapeDataString(segments[6])) : null);
    }

    private static string? NullIfWhiteSpace(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value;
    }
}
