using System.Globalization;
using System.Text.Json.Nodes;
using k8s.Models;
using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Kube;

internal static class KubeResourceSummaryFactory
{
    public static KubeResourceSummary Create(string contextName, KubeCustomResourceType customResourceType, JsonObject item)
    {
        var metadata = item["metadata"] as JsonObject;
        var labels = CreateLabels(metadata?["labels"] as JsonObject);
        var uid = metadata?["uid"]?.GetValue<string>();
        var createdAtUtc = TryParseDateTimeOffset(metadata?["creationTimestamp"]?.GetValue<string>());
        var namespaceName = customResourceType.Namespaced
            ? metadata?["namespace"]?.GetValue<string>()
            : null;
        var status = ResolveCustomResourceStatus(item["status"]);
        var summary = ResolveCustomResourceSummary(item["status"]);

        return new KubeResourceSummary(
            ContextName: contextName,
            Kind: KubeResourceKind.CustomResource,
            ApiVersion: item["apiVersion"]?.GetValue<string>() ?? customResourceType.ApiVersion,
            Name: metadata?["name"]?.GetValue<string>() ?? string.Empty,
            Namespace: namespaceName,
            Uid: uid,
            Status: status,
            Summary: summary,
            ReadyReplicas: null,
            DesiredReplicas: null,
            CreatedAtUtc: createdAtUtc,
            Labels: labels,
            CustomResourceType: customResourceType);
    }

    public static KubeResourceSummary Create(string contextName, V1Namespace item)
    {
        return CreateSummary(
            contextName,
            KubeResourceKind.Namespace,
            item,
            namespaceName: null,
            status: item.Status?.Phase,
            summary: item.Status?.Phase,
            readyReplicas: null,
            desiredReplicas: null);
    }

    public static KubeResourceSummary Create(string contextName, V1Node item)
    {
        var readyCondition = item.Status?.Conditions?
            .FirstOrDefault(condition => string.Equals(condition.Type, "Ready", StringComparison.OrdinalIgnoreCase));
        var isReady = string.Equals(readyCondition?.Status, "True", StringComparison.OrdinalIgnoreCase);
        var summary = item.Status?.NodeInfo is null
            ? readyCondition?.Message
            : $"{item.Status.NodeInfo.KernelVersion} | {item.Status.NodeInfo.ContainerRuntimeVersion}";

        return CreateSummary(
            contextName,
            KubeResourceKind.Node,
            item,
            namespaceName: null,
            status: isReady ? "Ready" : "NotReady",
            summary: summary,
            readyReplicas: isReady ? 1 : 0,
            desiredReplicas: 1);
    }

    public static KubeResourceSummary Create(string contextName, V1Pod item)
    {
        var desiredReplicas = item.Spec?.Containers?.Count;
        var readyReplicas = item.Status?.ContainerStatuses?.Count(status => status.Ready);
        var summary = BuildReplicaSummary(readyReplicas, desiredReplicas, item.Status?.Reason);

        return CreateSummary(
            contextName,
            KubeResourceKind.Pod,
            item,
            namespaceName: item.Metadata?.NamespaceProperty,
            status: item.Status?.Phase ?? item.Status?.Reason,
            summary: summary,
            readyReplicas: readyReplicas,
            desiredReplicas: desiredReplicas);
    }

    public static KubeResourceSummary Create(string contextName, V1Deployment item)
    {
        return CreateWorkloadSummary(
            contextName,
            KubeResourceKind.Deployment,
            item,
            item.Spec?.Replicas,
            item.Status?.ReadyReplicas,
            item.Status?.Conditions?.FirstOrDefault(condition => string.Equals(condition.Type, "Progressing", StringComparison.OrdinalIgnoreCase))?.Message);
    }

    public static KubeResourceSummary Create(string contextName, V1ReplicaSet item)
    {
        return CreateWorkloadSummary(
            contextName,
            KubeResourceKind.ReplicaSet,
            item,
            item.Spec?.Replicas,
            item.Status?.ReadyReplicas,
            item.Status?.Conditions?.FirstOrDefault(condition => string.Equals(condition.Type, "ReplicaFailure", StringComparison.OrdinalIgnoreCase))?.Message);
    }

    public static KubeResourceSummary Create(string contextName, V1StatefulSet item)
    {
        return CreateWorkloadSummary(
            contextName,
            KubeResourceKind.StatefulSet,
            item,
            item.Spec?.Replicas,
            item.Status?.ReadyReplicas,
            item.Status?.CurrentRevision);
    }

    public static KubeResourceSummary Create(string contextName, V1DaemonSet item)
    {
        var desiredReplicas = item.Status?.DesiredNumberScheduled;
        var readyReplicas = item.Status?.NumberReady;

        return CreateSummary(
            contextName,
            KubeResourceKind.DaemonSet,
            item,
            namespaceName: item.Metadata?.NamespaceProperty,
            status: BuildReplicaStatus(readyReplicas, desiredReplicas),
            summary: BuildReplicaSummary(readyReplicas, desiredReplicas, item.Status?.Conditions?.FirstOrDefault()?.Message),
            readyReplicas: readyReplicas,
            desiredReplicas: desiredReplicas);
    }

    public static KubeResourceSummary Create(string contextName, V1Service item)
    {
        var ports = item.Spec?.Ports?.Select(port => $"{port.Port}/{port.Protocol}").ToArray() ?? [];
        var summary = item.Spec?.Type switch
        {
            null => null,
            _ when ports.Length is 0 => item.Spec.Type,
            _ => $"{item.Spec.Type} | {string.Join(", ", ports)}"
        };

        return CreateSummary(
            contextName,
            KubeResourceKind.Service,
            item,
            namespaceName: item.Metadata?.NamespaceProperty,
            status: item.Spec?.Type,
            summary: summary,
            readyReplicas: null,
            desiredReplicas: null);
    }

    public static KubeResourceSummary Create(string contextName, V1Ingress item)
    {
        var hosts = item.Spec?.Rules?
            .Select(rule => rule.Host)
            .Where(static host => !string.IsNullOrWhiteSpace(host))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];

        return CreateSummary(
            contextName,
            KubeResourceKind.Ingress,
            item,
            namespaceName: item.Metadata?.NamespaceProperty,
            status: hosts.Length is 0 ? "Configured" : "HostsReady",
            summary: hosts.Length is 0 ? null : string.Join(", ", hosts),
            readyReplicas: null,
            desiredReplicas: null);
    }

    public static KubeResourceSummary Create(string contextName, V1ConfigMap item)
    {
        var keyCount = item.Data?.Count ?? 0;

        return CreateSummary(
            contextName,
            KubeResourceKind.ConfigMap,
            item,
            namespaceName: item.Metadata?.NamespaceProperty,
            status: "Present",
            summary: keyCount is 0 ? "No keys" : $"{keyCount} keys",
            readyReplicas: null,
            desiredReplicas: null);
    }

    public static KubeResourceSummary Create(string contextName, V1Secret item)
    {
        var keyCount = item.Data?.Count ?? 0;
        var summary = item.Type is null
            ? $"{keyCount} keys"
            : $"{item.Type} | {keyCount} keys";

        return CreateSummary(
            contextName,
            KubeResourceKind.Secret,
            item,
            namespaceName: item.Metadata?.NamespaceProperty,
            status: "Present",
            summary: summary,
            readyReplicas: null,
            desiredReplicas: null);
    }

    public static KubeResourceSummary Create(string contextName, V1Job item)
    {
        var status = item.Status?.Failed switch
        {
            > 0 => "Failed",
            _ when (item.Status?.Succeeded ?? 0) > 0 => "Succeeded",
            _ when (item.Status?.Active ?? 0) > 0 => "Running",
            _ => "Pending"
        };

        var summary = $"Succeeded {item.Status?.Succeeded ?? 0} | Failed {item.Status?.Failed ?? 0} | Active {item.Status?.Active ?? 0}";

        return CreateSummary(
            contextName,
            KubeResourceKind.Job,
            item,
            namespaceName: item.Metadata?.NamespaceProperty,
            status: status,
            summary: summary,
            readyReplicas: item.Status?.Succeeded,
            desiredReplicas: item.Spec?.Completions);
    }

    public static KubeResourceSummary Create(string contextName, V1CronJob item)
    {
        var status = item.Spec?.Suspend is true ? "Suspended" : "Scheduled";

        return CreateSummary(
            contextName,
            KubeResourceKind.CronJob,
            item,
            namespaceName: item.Metadata?.NamespaceProperty,
            status: status,
            summary: item.Spec?.Schedule,
            readyReplicas: null,
            desiredReplicas: null);
    }

    public static KubeResourceSummary Create(string contextName, Corev1Event item)
    {
        var status = string.IsNullOrWhiteSpace(item.Type)
            ? "Observed"
            : item.Type;
        var countPrefix = item.Count is > 1 ? $"x{item.Count} | " : string.Empty;
        var reason = string.IsNullOrWhiteSpace(item.Reason) ? "event" : item.Reason;
        var message = string.IsNullOrWhiteSpace(item.Message) ? null : Truncate(item.Message.Trim(), 96);
        var summary = string.IsNullOrWhiteSpace(message)
            ? $"{countPrefix}{reason}"
            : $"{countPrefix}{reason} | {message}";

        return CreateSummary(
            contextName,
            KubeResourceKind.Event,
            item,
            namespaceName: item.Metadata?.NamespaceProperty,
            status: status,
            summary: summary,
            readyReplicas: null,
            desiredReplicas: null);
    }

    public static bool MatchesSearch(KubeResourceSummary summary, string? search)
    {
        var searchTerms = KubeResourceSearchExpression.ParseAnyTerms(search);

        if (searchTerms.Count is 0)
        {
            return true;
        }

        return searchTerms.Any(term => MatchesSingleTerm(summary, term));
    }

    private static bool MatchesSingleTerm(KubeResourceSummary summary, string searchTerm)
    {
        return summary.Name.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
               (summary.CustomResourceType?.Kind.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (summary.CustomResourceType?.Plural.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (summary.Namespace?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (summary.Status?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (summary.Summary?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static KubeResourceSummary CreateWorkloadSummary<T>(
        string contextName,
        KubeResourceKind kind,
        T item,
        int? desiredReplicas,
        int? readyReplicas,
        string? fallbackSummary)
        where T : class, k8s.IKubernetesObject<V1ObjectMeta>
    {
        return CreateSummary(
            contextName,
            kind,
            item,
            namespaceName: item.Metadata?.NamespaceProperty,
            status: BuildReplicaStatus(readyReplicas, desiredReplicas),
            summary: BuildReplicaSummary(readyReplicas, desiredReplicas, fallbackSummary),
            readyReplicas: readyReplicas,
            desiredReplicas: desiredReplicas);
    }

    private static KubeResourceSummary CreateSummary<T>(
        string contextName,
        KubeResourceKind kind,
        T item,
        string? namespaceName,
        string? status,
        string? summary,
        int? readyReplicas,
        int? desiredReplicas)
        where T : class, k8s.IKubernetesObject<V1ObjectMeta>
    {
        var metadata = item.Metadata;
        var labels = metadata?.Labels is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(metadata.Labels, StringComparer.Ordinal);

        return new KubeResourceSummary(
            ContextName: contextName,
            Kind: kind,
            ApiVersion: item.ApiVersion ?? string.Empty,
            Name: metadata?.Name ?? string.Empty,
            Namespace: namespaceName,
            Uid: metadata?.Uid,
            Status: status,
            Summary: summary,
            ReadyReplicas: readyReplicas,
            DesiredReplicas: desiredReplicas,
            CreatedAtUtc: metadata?.CreationTimestamp,
            Labels: labels);
    }

    private static string? BuildReplicaStatus(int? readyReplicas, int? desiredReplicas)
    {
        if (desiredReplicas is null)
        {
            return null;
        }

        if (readyReplicas is null)
        {
            return $"{desiredReplicas} desired";
        }

        return readyReplicas >= desiredReplicas
            ? "Ready"
            : "Progressing";
    }

    private static string? BuildReplicaSummary(int? readyReplicas, int? desiredReplicas, string? fallback)
    {
        if (readyReplicas is not null || desiredReplicas is not null)
        {
            return $"{readyReplicas ?? 0}/{desiredReplicas ?? 0} ready";
        }

        return fallback;
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength
            ? value
            : $"{value[..(maxLength - 3)]}...";
    }

    private static IReadOnlyDictionary<string, string> CreateLabels(JsonObject? labelsObject)
    {
        if (labelsObject is null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var labels = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var property in labelsObject)
        {
            if (property.Value is not null && !string.IsNullOrWhiteSpace(property.Key))
            {
                labels[property.Key] = property.Value.ToString();
            }
        }

        return labels;
    }

    private static string? ResolveCustomResourceStatus(JsonNode? statusNode)
    {
        if (statusNode is not JsonObject statusObject)
        {
            return null;
        }

        if (statusObject["phase"]?.GetValue<string>() is { Length: > 0 } phase)
        {
            return phase;
        }

        if (statusObject["conditions"] is JsonArray conditions)
        {
            var readyCondition = conditions
                .OfType<JsonObject>()
                .FirstOrDefault(static condition =>
                    string.Equals(condition["type"]?.GetValue<string>(), "Ready", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(condition["type"]?.GetValue<string>(), "Established", StringComparison.OrdinalIgnoreCase));

            var readyStatus = readyCondition?["status"]?.GetValue<string>();

            if (!string.IsNullOrWhiteSpace(readyStatus))
            {
                return $"{readyCondition?["type"]?.GetValue<string>()}: {readyStatus}";
            }

            var firstCondition = conditions.OfType<JsonObject>().FirstOrDefault();
            var firstType = firstCondition?["type"]?.GetValue<string>();
            var firstStatus = firstCondition?["status"]?.GetValue<string>();

            if (!string.IsNullOrWhiteSpace(firstType) && !string.IsNullOrWhiteSpace(firstStatus))
            {
                return $"{firstType}: {firstStatus}";
            }
        }

        foreach (var property in statusObject)
        {
            if (property.Value is JsonValue)
            {
                return $"{property.Key}: {property.Value}";
            }
        }

        return null;
    }

    private static string? ResolveCustomResourceSummary(JsonNode? statusNode)
    {
        if (statusNode is not JsonObject statusObject)
        {
            return null;
        }

        if (statusObject["phase"]?.GetValue<string>() is { Length: > 0 } phase)
        {
            return $"Phase {phase}";
        }

        if (statusObject["conditions"] is JsonArray conditions)
        {
            var summary = conditions
                .OfType<JsonObject>()
                .Select(static condition =>
                {
                    var type = condition["type"]?.GetValue<string>();
                    var status = condition["status"]?.GetValue<string>();
                    var reason = condition["reason"]?.GetValue<string>();

                    if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(status))
                    {
                        return null;
                    }

                    return string.IsNullOrWhiteSpace(reason)
                        ? $"{type}={status}"
                        : $"{type}={status} ({reason})";
                })
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Take(3)
                .ToArray();

            if (summary.Length > 0)
            {
                return string.Join(" | ", summary);
            }
        }

        var scalarValues = statusObject
            .Where(static property => property.Value is JsonValue)
            .Take(3)
            .Select(static property => $"{property.Key}: {property.Value}")
            .ToArray();

        return scalarValues.Length is 0
            ? null
            : string.Join(" | ", scalarValues);
    }

    private static DateTimeOffset? TryParseDateTimeOffset(string? value)
    {
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }
}
