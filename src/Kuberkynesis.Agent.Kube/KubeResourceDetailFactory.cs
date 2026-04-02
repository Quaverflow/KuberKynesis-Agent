using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using k8s.Models;
using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Kube;

internal static class KubeResourceDetailFactory
{
    private static readonly JsonSerializerOptions RawManifestSerializerOptions = CreateRawManifestSerializerOptions();
    private static readonly JsonSerializerOptions EmbeddedJsonSerializerOptions = CreateEmbeddedJsonSerializerOptions();

    public static KubeResourceDetailResponse Create(string contextName, KubeCustomResourceType customResourceType, JsonObject item)
    {
        var summary = KubeResourceSummaryFactory.Create(contextName, customResourceType, item);
        var metadata = item["metadata"] as JsonObject;
        var statusObject = item["status"] as JsonObject;
        var specObject = item["spec"] as JsonObject;

        return new KubeResourceDetailResponse(
            Resource: summary,
            Sections: BuildCustomResourceSections(contextName, customResourceType, summary, metadata, specObject, statusObject),
            RelatedResources: CreateOwnerRelationsFromJson(metadata?["ownerReferences"] as JsonArray, summary.Namespace),
            Warnings: [],
            RawJson: KubeRawManifestFormatter.CreateJson(item),
            RawYaml: KubeRawManifestFormatter.CreateYaml(item),
            TransparencyCommands: []);
    }

    public static KubeResourceDetailResponse Create(string contextName, V1Namespace item, IReadOnlyList<KubeRelatedResource>? relatedResources = null)
    {
        var summary = KubeResourceSummaryFactory.Create(contextName, item);

        return CreateResponse(
            summary,
            item,
            sections:
            [
                CreateSection(
                    "Overview",
                    Field("Phase", item.Status?.Phase),
                    Field("Finalizers", item.Spec?.Finalizers?.Count))
            ],
            relatedResources);
    }

    public static KubeResourceDetailResponse Create(string contextName, V1Node item, IReadOnlyList<KubeRelatedResource>? relatedResources = null)
    {
        var summary = KubeResourceSummaryFactory.Create(contextName, item);

        return CreateResponse(
            summary,
            item,
            sections:
            [
                CreateSection(
                    "Overview",
                    Field("Ready", summary.Status),
                    Field("Kubelet version", item.Status?.NodeInfo?.KubeletVersion),
                    Field("Kernel", item.Status?.NodeInfo?.KernelVersion),
                    Field("OS image", item.Status?.NodeInfo?.OsImage),
                    Field("Architecture", item.Status?.NodeInfo?.Architecture),
                    Field("Pod CIDR", item.Spec?.PodCIDR)),
                CreateConditionsSection(
                    "Conditions",
                    item.Status?.Conditions,
                    static condition => condition.Type,
                    static condition => condition.Status,
                    static condition => condition.Reason,
                    static condition => condition.Message)
            ],
            relatedResources);
    }

    public static KubeResourceDetailResponse Create(string contextName, V1Pod item, IReadOnlyList<KubeRelatedResource>? relatedResources = null)
    {
        var summary = KubeResourceSummaryFactory.Create(contextName, item);
        var restartCount = item.Status?.ContainerStatuses?.Sum(status => status.RestartCount);
        var podRelations = new List<KubeRelatedResource>();

        if (!string.IsNullOrWhiteSpace(item.Spec?.NodeName))
        {
            podRelations.Add(new KubeRelatedResource(
                Relationship: "Scheduled on",
                Kind: KubeResourceKind.Node,
                ApiVersion: "v1",
                Name: item.Spec.NodeName,
                Namespace: null,
                Status: null,
                Summary: item.Status?.HostIP));
        }

        podRelations.AddRange(CreateConfigurationRelations(summary.Namespace, item.Spec));

        return CreateResponse(
            summary,
            item,
            sections:
            [
                CreateSection(
                    "Overview",
                    Field("Phase", item.Status?.Phase),
                    Field("Ready", GetPodConditionSummary(item.Status?.Conditions, "Ready")),
                    Field("Pod scheduled", GetPodConditionSummary(item.Status?.Conditions, "PodScheduled")),
                    Field("Pod IP", item.Status?.PodIP),
                    Field("Host IP", item.Status?.HostIP),
                    Field("Priority class", item.Spec?.PriorityClassName),
                    Field("Service account", item.Spec?.ServiceAccountName),
                    Field("Node", item.Spec?.NodeName),
                    Field("QoS class", item.Status?.QosClass),
                    Field("Restarts", restartCount)),
                CreateNamedValueSection(
                    "Containers",
                    item.Spec?.Containers?.Select(container => new KeyValuePair<string, string?>(container.Name, container.Image))),
                CreateNamedValueSection(
                    "Init containers",
                    item.Spec?.InitContainers?.Select(container => new KeyValuePair<string, string?>(container.Name, container.Image))),
                CreatePodContainerStatusSection(item),
                CreateNamedValueSection(
                    "Container commands",
                    CreatePodContainerCommandEntries(item.Spec)),
                CreateNamedValueSection(
                    "Container resources",
                    CreatePodContainerResourceEntries(item.Spec)),
                CreateNamedValueSection(
                    "Container probes",
                    CreatePodContainerProbeEntries(item.Spec)),
                CreateNamedValueSection(
                    "Volume mounts",
                    CreatePodVolumeMountEntries(item.Spec)),
                CreateNamedValueSection(
                    "Declared ports",
                    item.Spec?.Containers?.SelectMany(container =>
                        (container.Ports ?? [])
                            .Select(port => new KeyValuePair<string, string?>(
                                GetPodPortLabel(container.Name, port.Name, port.ContainerPort),
                                $"{port.ContainerPort}/{port.Protocol ?? "TCP"}")))),
                CreateNamedValueSection(
                    "Environment sources",
                    EnumeratePodContainers(item.Spec).SelectMany(static containerEntry => CreatePodEnvironmentSourceEntries(containerEntry.Container, containerEntry.Role))),
                CreateNamedValueSection(
                    "Volumes",
                    CreatePodVolumeEntries(item.Spec)),
                CreateSection(
                    "Scheduling",
                    Field("Scheduler", item.Spec?.SchedulerName),
                    Field("Host network", item.Spec?.HostNetwork),
                    Field("DNS policy", item.Spec?.DnsPolicy),
                    Field("Node selectors", item.Spec?.NodeSelector?.Count),
                    Field("Topology spread constraints", item.Spec?.TopologySpreadConstraints?.Count),
                    Field("Tolerations", item.Spec?.Tolerations?.Count)),
                CreateNamedValueSection(
                    "Tolerations",
                    CreatePodTolerationEntries(item.Spec)),
                CreateNamedValueSection(
                    "Affinity and spread",
                    CreatePodAffinityEntries(item.Spec)),
                CreateSection(
                    "Health and lifecycle",
                    Field("Ready containers", GetReadyContainerSummary(item.Status?.ContainerStatuses)),
                    Field("Init containers ready", GetReadyContainerSummary(item.Status?.InitContainerStatuses)),
                    Field("Waiting containers", GetCurrentContainerStateSummary(item.Status?.ContainerStatuses, requireWaitingState: true)),
                    Field("Last termination", GetLastTerminationSummary(item.Status?.ContainerStatuses)),
                    Field("Pod status reason", item.Status?.Reason),
                    Field("Pod status message", item.Status?.Message)),
                CreateConditionsSection(
                    "Conditions",
                    item.Status?.Conditions,
                    static condition => condition.Type,
                    static condition => condition.Status,
                    static condition => condition.Reason,
                    static condition => condition.Message)
            ],
            relatedResources,
            extraRelations: podRelations);
    }

    public static KubeResourceDetailResponse Create(string contextName, V1Deployment item, IReadOnlyList<KubeRelatedResource>? relatedResources = null)
    {
        var summary = KubeResourceSummaryFactory.Create(contextName, item);
        var configurationRelations = CreateConfigurationRelations(summary.Namespace, item.Spec?.Template?.Spec);

        return CreateResponse(
            summary,
            item,
            sections:
            [
                CreateSection(
                    "Overview",
                    Field("Status", summary.Status),
                    Field("Replicas", summary.Summary),
                    Field("Strategy", item.Spec?.Strategy?.Type),
                    Field("Paused", item.Spec?.Paused)),
                CreateNamedValueSection("Selector", item.Spec?.Selector?.MatchLabels),
                CreateNamedValueSection(
                    "Containers",
                    item.Spec?.Template?.Spec?.Containers?.Select(container => new KeyValuePair<string, string?>(container.Name, container.Image))),
                CreateConditionsSection(
                    "Conditions",
                    item.Status?.Conditions,
                    static condition => condition.Type,
                    static condition => condition.Status,
                    static condition => condition.Reason,
                    static condition => condition.Message)
            ],
            relatedResources,
            extraRelations: configurationRelations);
    }

    public static KubeResourceDetailResponse Create(string contextName, V1ReplicaSet item, IReadOnlyList<KubeRelatedResource>? relatedResources = null)
    {
        var summary = KubeResourceSummaryFactory.Create(contextName, item);
        var configurationRelations = CreateConfigurationRelations(summary.Namespace, item.Spec?.Template?.Spec);

        return CreateResponse(
            summary,
            item,
            sections:
            [
                CreateSection(
                    "Overview",
                    Field("Status", summary.Status),
                    Field("Replicas", summary.Summary),
                    Field("Min ready seconds", item.Spec?.MinReadySeconds)),
                CreateNamedValueSection("Selector", item.Spec?.Selector?.MatchLabels),
                CreateNamedValueSection(
                    "Containers",
                    item.Spec?.Template?.Spec?.Containers?.Select(container => new KeyValuePair<string, string?>(container.Name, container.Image))),
                CreateConditionsSection(
                    "Conditions",
                    item.Status?.Conditions,
                    static condition => condition.Type,
                    static condition => condition.Status,
                    static condition => condition.Reason,
                    static condition => condition.Message)
            ],
            relatedResources,
            extraRelations: configurationRelations);
    }

    public static KubeResourceDetailResponse Create(string contextName, V1StatefulSet item, IReadOnlyList<KubeRelatedResource>? relatedResources = null)
    {
        var summary = KubeResourceSummaryFactory.Create(contextName, item);
        var configurationRelations = CreateConfigurationRelations(summary.Namespace, item.Spec?.Template?.Spec);

        return CreateResponse(
            summary,
            item,
            sections:
            [
                CreateSection(
                    "Overview",
                    Field("Status", summary.Status),
                    Field("Replicas", summary.Summary),
                    Field("Service name", item.Spec?.ServiceName),
                    Field("Update strategy", item.Spec?.UpdateStrategy?.Type),
                    Field("Current revision", item.Status?.CurrentRevision)),
                CreateNamedValueSection("Selector", item.Spec?.Selector?.MatchLabels),
                CreateNamedValueSection(
                    "Containers",
                    item.Spec?.Template?.Spec?.Containers?.Select(container => new KeyValuePair<string, string?>(container.Name, container.Image)))
            ],
            relatedResources,
            extraRelations: configurationRelations);
    }

    public static KubeResourceDetailResponse Create(string contextName, V1DaemonSet item, IReadOnlyList<KubeRelatedResource>? relatedResources = null)
    {
        var summary = KubeResourceSummaryFactory.Create(contextName, item);
        var configurationRelations = CreateConfigurationRelations(summary.Namespace, item.Spec?.Template?.Spec);

        return CreateResponse(
            summary,
            item,
            sections:
            [
                CreateSection(
                    "Overview",
                    Field("Status", summary.Status),
                    Field("Replicas", summary.Summary),
                    Field("Update strategy", item.Spec?.UpdateStrategy?.Type)),
                CreateNamedValueSection("Selector", item.Spec?.Selector?.MatchLabels),
                CreateNamedValueSection(
                    "Containers",
                    item.Spec?.Template?.Spec?.Containers?.Select(container => new KeyValuePair<string, string?>(container.Name, container.Image))),
                CreateConditionsSection(
                    "Conditions",
                    item.Status?.Conditions,
                    static condition => condition.Type,
                    static condition => condition.Status,
                    static condition => condition.Reason,
                    static condition => condition.Message)
            ],
            relatedResources,
            extraRelations: configurationRelations);
    }

    public static KubeResourceDetailResponse Create(string contextName, V1Service item, IReadOnlyList<KubeRelatedResource>? relatedResources = null)
    {
        var summary = KubeResourceSummaryFactory.Create(contextName, item);
        var externalIps = item.Spec?.ExternalIPs?.Count > 0
            ? string.Join(", ", item.Spec.ExternalIPs)
            : null;
        var clusterIps = item.Spec?.ClusterIPs?.Count > 0
            ? string.Join(", ", item.Spec.ClusterIPs)
            : item.Spec?.ClusterIP;

        return CreateResponse(
            summary,
            item,
            sections:
            [
                CreateSection(
                    "Overview",
                    Field("Type", item.Spec?.Type),
                    Field("Cluster IP", clusterIps),
                    Field("External IPs", externalIps),
                    Field("Session affinity", item.Spec?.SessionAffinity)),
                CreateNamedValueSection("Selector", item.Spec?.Selector),
                CreateNamedValueSection(
                    "Ports",
                    item.Spec?.Ports?.Select(port => new KeyValuePair<string, string?>(
                        port.Name ?? port.Port.ToString(CultureInfo.InvariantCulture),
                        $"{port.Port}/{port.Protocol}")))
            ],
            relatedResources);
    }

    public static KubeResourceDetailResponse Create(string contextName, V1Ingress item, IReadOnlyList<KubeRelatedResource>? relatedResources = null)
    {
        var summary = KubeResourceSummaryFactory.Create(contextName, item);
        var addresses = item.Status?.LoadBalancer?.Ingress?
            .Select(ingress => ingress.Hostname ?? ingress.Ip)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        var tlsHosts = item.Spec?.Tls?
            .SelectMany(tls => tls.Hosts ?? [])
            .Where(static host => !string.IsNullOrWhiteSpace(host))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return CreateResponse(
            summary,
            item,
            sections:
            [
                CreateSection(
                    "Overview",
                    Field("Class", item.Spec?.IngressClassName),
                    Field("Hosts", summary.Summary),
                    Field("Addresses", addresses?.Length > 0 ? string.Join(", ", addresses) : null),
                    Field("TLS hosts", tlsHosts?.Length > 0 ? string.Join(", ", tlsHosts) : null))
            ],
            relatedResources);
    }

    public static KubeResourceDetailResponse Create(string contextName, V1ConfigMap item, IReadOnlyList<KubeRelatedResource>? relatedResources = null)
    {
        var summary = KubeResourceSummaryFactory.Create(contextName, item);

        return CreateResponse(
            summary,
            item,
            sections:
            [
                CreateSection(
                    "Overview",
                    Field("Keys", item.Data?.Count ?? 0),
                    Field("Immutable", item.Immutable)),
                CreateNamedValueSection(
                    "Data keys",
                    item.Data?.Keys.Select(key => new KeyValuePair<string, string?>(key, "present")))
            ],
            relatedResources);
    }

    public static KubeResourceDetailResponse Create(string contextName, V1Secret item, IReadOnlyList<KubeRelatedResource>? relatedResources = null)
    {
        var summary = KubeResourceSummaryFactory.Create(contextName, item);
        var keyNames = item.Data?.Keys ?? [];

        return CreateResponse(
            summary,
            item,
            sections:
            [
                CreateSection(
                    "Overview",
                    Field("Type", item.Type),
                    Field("Keys", keyNames.Count),
                    Field("Immutable", item.Immutable)),
                CreateNamedValueSection(
                    "Secret keys",
                    keyNames.Select(key => new KeyValuePair<string, string?>(key, "metadata only")))
            ],
            relatedResources);
    }

    public static KubeResourceDetailResponse Create(string contextName, V1Job item, IReadOnlyList<KubeRelatedResource>? relatedResources = null)
    {
        var summary = KubeResourceSummaryFactory.Create(contextName, item);
        var configurationRelations = CreateConfigurationRelations(summary.Namespace, item.Spec?.Template?.Spec);

        return CreateResponse(
            summary,
            item,
            sections:
            [
                CreateSection(
                    "Overview",
                    Field("Status", summary.Status),
                    Field("Progress", summary.Summary),
                    Field("Completions", item.Spec?.Completions),
                    Field("Parallelism", item.Spec?.Parallelism),
                    Field("Backoff limit", item.Spec?.BackoffLimit))
            ],
            relatedResources,
            extraRelations: configurationRelations);
    }

    public static KubeResourceDetailResponse Create(string contextName, V1CronJob item, IReadOnlyList<KubeRelatedResource>? relatedResources = null)
    {
        var summary = KubeResourceSummaryFactory.Create(contextName, item);
        var configurationRelations = CreateConfigurationRelations(summary.Namespace, item.Spec?.JobTemplate?.Spec?.Template?.Spec);

        return CreateResponse(
            summary,
            item,
            sections:
            [
                CreateSection(
                    "Overview",
                    Field("Status", summary.Status),
                    Field("Schedule", item.Spec?.Schedule),
                    Field("Time zone", item.Spec?.TimeZone),
                    Field("Concurrency", item.Spec?.ConcurrencyPolicy),
                    Field("Suspend", item.Spec?.Suspend))
            ],
            relatedResources,
            extraRelations: configurationRelations);
    }

    public static KubeResourceDetailResponse Create(string contextName, Corev1Event item, IReadOnlyList<KubeRelatedResource>? relatedResources = null)
    {
        var summary = KubeResourceSummaryFactory.Create(contextName, item);
        var eventRelations = new List<KubeRelatedResource>();

        var involvedObject = CreateObjectReferenceRelation("Regarding", item.InvolvedObject);

        if (involvedObject is not null)
        {
            eventRelations.Add(involvedObject);
        }

        var relatedObject = CreateObjectReferenceRelation("Related object", item.Related);

        if (relatedObject is not null)
        {
            eventRelations.Add(relatedObject);
        }

        return CreateResponse(
            summary,
            item,
            sections:
            [
                CreateSection(
                    "Overview",
                    Field("Type", item.Type),
                    Field("Reason", item.Reason),
                    Field("Action", item.Action),
                    Field("Count", item.Count)),
                CreateSection(
                    "Source",
                    Field("Component", item.Source?.Component),
                    Field("Host", item.Source?.Host)),
                CreateSection(
                    "Timing",
                    Field("Event time", item.EventTime),
                    Field("First seen", item.FirstTimestamp),
                    Field("Last seen", item.LastTimestamp)),
                CreateSection(
                    "Message",
                    Field("Details", item.Message))
            ],
            relatedResources,
            extraRelations: eventRelations);
    }

    private static KubeResourceDetailResponse CreateResponse<T>(
        KubeResourceSummary summary,
        T item,
        IEnumerable<KubeResourceDetailSection?> sections,
        IReadOnlyList<KubeRelatedResource>? relatedResources,
        IReadOnlyList<KubeRelatedResource>? extraRelations = null,
        IReadOnlyList<KubectlCommandPreview>? transparencyCommands = null)
        where T : class, k8s.IKubernetesObject<V1ObjectMeta>
    {
        var mergedRelatedResources = new List<KubeRelatedResource>();

        mergedRelatedResources.AddRange(CreateOwnerRelations(item.Metadata?.OwnerReferences, summary.Namespace));

        if (extraRelations is not null)
        {
            mergedRelatedResources.AddRange(extraRelations);
        }

        if (relatedResources is not null)
        {
            mergedRelatedResources.AddRange(relatedResources);
        }

        var finalSections = new List<KubeResourceDetailSection>
        {
            CreateMetadataSection(summary, item)
        };

        finalSections.AddRange(sections.Where(section => section is not null)!);

        var labelsSection = CreateNamedValueSection("Labels", item.Metadata?.Labels);
        var annotationsSection = CreateNamedValueSection("Annotations", item.Metadata?.Annotations);

        if (labelsSection is not null)
        {
            finalSections.Add(labelsSection);
        }

        if (annotationsSection is not null)
        {
            finalSections.Add(annotationsSection);
        }

        return new KubeResourceDetailResponse(
            Resource: summary,
            Sections: finalSections,
            RelatedResources: mergedRelatedResources
                .GroupBy(resource => $"{resource.Relationship}|{resource.Kind}|{resource.Namespace}|{resource.Name}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray(),
            Warnings: [],
            RawJson: KubeRawManifestFormatter.CreateJson(CreateRawManifestNode(item)),
            RawYaml: KubeRawManifestFormatter.CreateYaml(CreateRawManifestNode(item)),
            TransparencyCommands: transparencyCommands ?? []);
    }

    private static KubeResourceDetailSection CreateMetadataSection<T>(KubeResourceSummary summary, T item)
        where T : class, k8s.IKubernetesObject<V1ObjectMeta>
    {
        return CreateSection(
            "Metadata",
            Field("Context", summary.ContextName),
            Field("Kind", summary.Kind.ToString()),
            Field("API version", item.ApiVersion),
            Field("Namespace", summary.Namespace ?? "cluster-scoped"),
            Field("UID", summary.Uid),
            Field("Created", summary.CreatedAtUtc?.ToLocalTime().ToString("u", CultureInfo.InvariantCulture)));
    }

    private static KubeResourceDetailSection? CreateConditionsSection<TCondition>(
        string title,
        IEnumerable<TCondition>? conditions,
        Func<TCondition, string?> typeSelector,
        Func<TCondition, string?> statusSelector,
        Func<TCondition, string?> reasonSelector,
        Func<TCondition, string?> messageSelector)
    {
        if (conditions is null)
        {
            return null;
        }

        var fields = conditions
            .Select(condition =>
            {
                var type = typeSelector(condition);
                var status = statusSelector(condition);
                var reason = reasonSelector(condition);
                var message = messageSelector(condition);
                var parts = new[] { status, reason, message }
                    .Where(static part => !string.IsNullOrWhiteSpace(part))
                    .ToArray();

                return string.IsNullOrWhiteSpace(type) || parts.Length is 0
                    ? null
                    : new KubeResourceDetailField(type, string.Join(" | ", parts));
            })
            .Where(field => field is not null)
            .Cast<KubeResourceDetailField>()
            .ToArray();

        return fields.Length is 0
            ? null
            : new KubeResourceDetailSection(title, fields);
    }

    private static KubeResourceDetailSection? CreateNamedValueSection(
        string title,
        IEnumerable<KeyValuePair<string, string?>>? values)
    {
        if (values is null)
        {
            return null;
        }

        var fields = values
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new KubeResourceDetailField(pair.Key, pair.Value!))
            .ToArray();

        return fields.Length is 0
            ? null
            : new KubeResourceDetailSection(title, fields);
    }

    private static KubeResourceDetailSection? CreatePodContainerStatusSection(V1Pod item)
    {
        if (item.Status?.ContainerStatuses is null)
        {
            return null;
        }

        var fields = item.Status.ContainerStatuses
            .Select(status =>
            {
                var state = DescribeContainerState(status.State);
                var parts = new List<string>
                {
                    status.Ready ? "ready" : "not ready",
                    $"restarts {status.RestartCount}"
                };

                if (!string.IsNullOrWhiteSpace(state))
                {
                    parts.Add(state);
                }

                return new KubeResourceDetailField(status.Name, string.Join(" | ", parts));
            })
            .ToArray();

        return fields.Length is 0
            ? null
            : new KubeResourceDetailSection("Container status", fields);
    }

    private static KubeResourceDetailSection CreateSection(string title, params KubeResourceDetailField?[] fields)
    {
        return new KubeResourceDetailSection(
            title,
            fields
                .Where(field => field is not null)
                .Cast<KubeResourceDetailField>()
                .ToArray());
    }

    private static KubeResourceDetailField? Field(string label, object? value)
    {
        if (value is null)
        {
            return null;
        }

        var formatted = value switch
        {
            bool booleanValue => booleanValue ? "Yes" : "No",
            DateTime timestamp => timestamp.ToUniversalTime().ToString("u", CultureInfo.InvariantCulture),
            DateTimeOffset timestamp => timestamp.ToLocalTime().ToString("u", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)
        };

        return string.IsNullOrWhiteSpace(formatted)
            ? null
            : new KubeResourceDetailField(label, formatted);
    }

    private static string GetPodPortLabel(string containerName, string? portName, int portNumber)
    {
        var suffix = string.IsNullOrWhiteSpace(portName)
            ? portNumber.ToString(CultureInfo.InvariantCulture)
            : portName;

        return $"{containerName} / {suffix}";
    }

    private static IEnumerable<KeyValuePair<string, string?>> CreatePodEnvironmentSourceEntries(V1Container container, string role)
    {
        var containerLabel = GetPodContainerDisplayName(container.Name, role);

        foreach (var envSource in container.EnvFrom ?? [])
        {
            if (!string.IsNullOrWhiteSpace(envSource.ConfigMapRef?.Name))
            {
                yield return new KeyValuePair<string, string?>($"{containerLabel} / ConfigMap", envSource.ConfigMapRef.Name);
            }

            if (!string.IsNullOrWhiteSpace(envSource.SecretRef?.Name))
            {
                yield return new KeyValuePair<string, string?>($"{containerLabel} / Secret", envSource.SecretRef.Name);
            }
        }

        foreach (var environmentVariable in container.Env ?? [])
        {
            if (!string.IsNullOrWhiteSpace(environmentVariable.ValueFrom?.ConfigMapKeyRef?.Name))
            {
                yield return new KeyValuePair<string, string?>(
                    $"{containerLabel} / env {environmentVariable.Name}",
                    $"ConfigMap/{environmentVariable.ValueFrom.ConfigMapKeyRef.Name}:{environmentVariable.ValueFrom.ConfigMapKeyRef.Key}");
            }

            if (!string.IsNullOrWhiteSpace(environmentVariable.ValueFrom?.SecretKeyRef?.Name))
            {
                yield return new KeyValuePair<string, string?>(
                    $"{containerLabel} / env {environmentVariable.Name}",
                    $"Secret/{environmentVariable.ValueFrom.SecretKeyRef.Name}:{environmentVariable.ValueFrom.SecretKeyRef.Key}");
            }

            if (!string.IsNullOrWhiteSpace(environmentVariable.ValueFrom?.FieldRef?.FieldPath))
            {
                yield return new KeyValuePair<string, string?>(
                    $"{containerLabel} / env {environmentVariable.Name}",
                    $"fieldRef {environmentVariable.ValueFrom.FieldRef.FieldPath}");
            }

            if (!string.IsNullOrWhiteSpace(environmentVariable.ValueFrom?.ResourceFieldRef?.Resource))
            {
                yield return new KeyValuePair<string, string?>(
                    $"{containerLabel} / env {environmentVariable.Name}",
                    $"resourceFieldRef {environmentVariable.ValueFrom.ResourceFieldRef.Resource}");
            }
        }
    }

    private static IEnumerable<(string Role, V1Container Container)> EnumeratePodContainers(V1PodSpec? podSpec)
    {
        if (podSpec is null)
        {
            yield break;
        }

        foreach (var initContainer in podSpec.InitContainers ?? [])
        {
            yield return ("init", initContainer);
        }

        foreach (var container in podSpec.Containers ?? [])
        {
            yield return ("main", container);
        }
    }

    private static IEnumerable<KeyValuePair<string, string?>> CreatePodContainerCommandEntries(V1PodSpec? podSpec)
    {
        foreach (var (role, container) in EnumeratePodContainers(podSpec))
        {
            var commandParts = (container.Command ?? [])
                .Concat(container.Args ?? [])
                .Where(static part => !string.IsNullOrWhiteSpace(part))
                .ToArray();

            yield return new KeyValuePair<string, string?>(
                GetPodContainerDisplayName(container.Name, role),
                commandParts.Length is 0
                    ? "image defaults"
                    : string.Join(" ", commandParts));
        }
    }

    private static IEnumerable<KeyValuePair<string, string?>> CreatePodContainerResourceEntries(V1PodSpec? podSpec)
    {
        foreach (var (role, container) in EnumeratePodContainers(podSpec))
        {
            var containerLabel = GetPodContainerDisplayName(container.Name, role);
            yield return new KeyValuePair<string, string?>(
                $"{containerLabel} / requests",
                FormatResourceRequirements(container.Resources?.Requests));
            yield return new KeyValuePair<string, string?>(
                $"{containerLabel} / limits",
                FormatResourceRequirements(container.Resources?.Limits));
        }
    }

    private static IEnumerable<KeyValuePair<string, string?>> CreatePodContainerProbeEntries(V1PodSpec? podSpec)
    {
        foreach (var (role, container) in EnumeratePodContainers(podSpec))
        {
            var containerLabel = GetPodContainerDisplayName(container.Name, role);
            yield return new KeyValuePair<string, string?>($"{containerLabel} / readiness", DescribeProbe(container.ReadinessProbe));
            yield return new KeyValuePair<string, string?>($"{containerLabel} / liveness", DescribeProbe(container.LivenessProbe));
            yield return new KeyValuePair<string, string?>($"{containerLabel} / startup", DescribeProbe(container.StartupProbe));
        }
    }

    private static IEnumerable<KeyValuePair<string, string?>> CreatePodVolumeMountEntries(V1PodSpec? podSpec)
    {
        foreach (var (role, container) in EnumeratePodContainers(podSpec))
        {
            var containerLabel = GetPodContainerDisplayName(container.Name, role);

            foreach (var mount in container.VolumeMounts ?? [])
            {
                var mountFlags = new List<string>();

                if (mount.ReadOnlyProperty is true)
                {
                    mountFlags.Add("read-only");
                }

                if (!string.IsNullOrWhiteSpace(mount.SubPath))
                {
                    mountFlags.Add($"subPath {mount.SubPath}");
                }

                var suffix = mountFlags.Count is 0
                    ? string.Empty
                    : $" ({string.Join(", ", mountFlags)})";

                yield return new KeyValuePair<string, string?>(
                    $"{containerLabel} / {mount.MountPath}",
                    $"{mount.Name}{suffix}");
            }
        }
    }

    private static IEnumerable<KeyValuePair<string, string?>> CreatePodVolumeEntries(V1PodSpec? podSpec)
    {
        foreach (var volume in podSpec?.Volumes ?? [])
        {
            yield return new KeyValuePair<string, string?>(volume.Name, DescribeVolume(volume));
        }
    }

    private static IEnumerable<KeyValuePair<string, string?>> CreatePodTolerationEntries(V1PodSpec? podSpec)
    {
        foreach (var toleration in podSpec?.Tolerations ?? [])
        {
            var keyLabel = string.IsNullOrWhiteSpace(toleration.Key)
                ? "<all keys>"
                : toleration.Key;
            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(toleration.OperatorProperty))
            {
                parts.Add(toleration.OperatorProperty);
            }

            if (!string.IsNullOrWhiteSpace(toleration.Value))
            {
                parts.Add(toleration.Value);
            }

            if (!string.IsNullOrWhiteSpace(toleration.Effect))
            {
                parts.Add(toleration.Effect);
            }

            if (toleration.TolerationSeconds.HasValue)
            {
                parts.Add($"{toleration.TolerationSeconds.Value}s");
            }

            yield return new KeyValuePair<string, string?>(
                keyLabel,
                parts.Count is 0 ? "present" : string.Join(" | ", parts));
        }
    }

    private static IEnumerable<KeyValuePair<string, string?>> CreatePodAffinityEntries(V1PodSpec? podSpec)
    {
        if (podSpec?.NodeSelector?.Count > 0)
        {
            yield return new KeyValuePair<string, string?>(
                "Node selector",
                string.Join(", ", podSpec.NodeSelector.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => $"{pair.Key}={pair.Value}")));
        }

        var affinity = podSpec?.Affinity;

        if (affinity?.NodeAffinity is not null)
        {
            yield return new KeyValuePair<string, string?>(
                "Node affinity",
                DescribeAffinityPresence(
                    affinity.NodeAffinity.RequiredDuringSchedulingIgnoredDuringExecution is not null,
                    affinity.NodeAffinity.PreferredDuringSchedulingIgnoredDuringExecution?.Count > 0));
        }

        if (affinity?.PodAffinity is not null)
        {
            yield return new KeyValuePair<string, string?>(
                "Pod affinity",
                DescribeAffinityPresence(
                    affinity.PodAffinity.RequiredDuringSchedulingIgnoredDuringExecution?.Count > 0,
                    affinity.PodAffinity.PreferredDuringSchedulingIgnoredDuringExecution?.Count > 0));
        }

        if (affinity?.PodAntiAffinity is not null)
        {
            yield return new KeyValuePair<string, string?>(
                "Pod anti-affinity",
                DescribeAffinityPresence(
                    affinity.PodAntiAffinity.RequiredDuringSchedulingIgnoredDuringExecution?.Count > 0,
                    affinity.PodAntiAffinity.PreferredDuringSchedulingIgnoredDuringExecution?.Count > 0));
        }

        foreach (var spreadConstraint in podSpec?.TopologySpreadConstraints ?? [])
        {
            yield return new KeyValuePair<string, string?>(
                $"Topology spread / {spreadConstraint.TopologyKey}",
                $"max skew {spreadConstraint.MaxSkew}, {spreadConstraint.WhenUnsatisfiable}");
        }
    }

    private static IReadOnlyList<KubeRelatedResource> CreateConfigurationRelations(string? namespaceName, V1PodSpec? podSpec)
    {
        if (podSpec is null)
        {
            return [];
        }

        var references = new Dictionary<(KubeResourceKind Kind, string Name), HashSet<string>>();

        foreach (var container in (podSpec.InitContainers ?? []).Concat(podSpec.Containers ?? []))
        {
            foreach (var envSource in container.EnvFrom ?? [])
            {
                AddConfigurationReference(references, KubeResourceKind.ConfigMap, envSource.ConfigMapRef?.Name, "environment");
                AddConfigurationReference(references, KubeResourceKind.Secret, envSource.SecretRef?.Name, "environment");
            }

            foreach (var environmentVariable in container.Env ?? [])
            {
                AddConfigurationReference(references, KubeResourceKind.ConfigMap, environmentVariable.ValueFrom?.ConfigMapKeyRef?.Name, "environment");
                AddConfigurationReference(references, KubeResourceKind.Secret, environmentVariable.ValueFrom?.SecretKeyRef?.Name, "environment");
            }
        }

        foreach (var volume in podSpec.Volumes ?? [])
        {
            AddConfigurationReference(references, KubeResourceKind.ConfigMap, volume.ConfigMap?.Name, "volume");
            AddConfigurationReference(references, KubeResourceKind.Secret, volume.Secret?.SecretName, "volume");

            foreach (var source in volume.Projected?.Sources ?? [])
            {
                AddConfigurationReference(references, KubeResourceKind.ConfigMap, source.ConfigMap?.Name, "projected volume");
                AddConfigurationReference(references, KubeResourceKind.Secret, source.Secret?.Name, "projected volume");
            }
        }

        foreach (var imagePullSecret in podSpec.ImagePullSecrets ?? [])
        {
            AddConfigurationReference(references, KubeResourceKind.Secret, imagePullSecret.Name, "image pull");
        }

        return references
            .OrderBy(reference => reference.Key.Kind.ToString(), StringComparer.OrdinalIgnoreCase)
            .ThenBy(reference => reference.Key.Name, StringComparer.OrdinalIgnoreCase)
            .Select(reference => new KubeRelatedResource(
                Relationship: reference.Key.Kind is KubeResourceKind.ConfigMap ? "Uses ConfigMap" : "Uses Secret",
                Kind: reference.Key.Kind,
                ApiVersion: "v1",
                Name: reference.Key.Name,
                Namespace: namespaceName,
                Status: null,
                Summary: BuildConfigurationUsageSummary(reference.Value)))
            .ToArray();
    }

    private static void AddConfigurationReference(
        IDictionary<(KubeResourceKind Kind, string Name), HashSet<string>> references,
        KubeResourceKind kind,
        string? name,
        string usage)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var key = (kind, name.Trim());

        if (!references.TryGetValue(key, out var usages))
        {
            usages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            references[key] = usages;
        }

        usages.Add(usage);
    }

    private static string BuildConfigurationUsageSummary(IEnumerable<string> usages)
    {
        return string.Join(
            ", ",
            usages
                .OrderBy(static usage => usage, StringComparer.OrdinalIgnoreCase));
    }

    private static string GetPodContainerDisplayName(string containerName, string role)
    {
        return string.Equals(role, "init", StringComparison.OrdinalIgnoreCase)
            ? $"{containerName} (init)"
            : containerName;
    }

    private static string? FormatResourceRequirements(IDictionary<string, ResourceQuantity>? requirements)
    {
        if (requirements is null || requirements.Count is 0)
        {
            return null;
        }

        return string.Join(
            ", ",
            requirements
                .OrderBy(requirement => requirement.Key, StringComparer.OrdinalIgnoreCase)
                .Select(requirement => $"{requirement.Key}={requirement.Value}"));
    }

    private static string? DescribeProbe(V1Probe? probe)
    {
        if (probe is null)
        {
            return null;
        }

        var handler = DescribeProbeHandler(probe);
        var timings = new List<string>();

        if (probe.InitialDelaySeconds.HasValue)
        {
            timings.Add($"delay {probe.InitialDelaySeconds.Value}s");
        }

        if (probe.PeriodSeconds.HasValue)
        {
            timings.Add($"period {probe.PeriodSeconds.Value}s");
        }

        if (probe.TimeoutSeconds.HasValue)
        {
            timings.Add($"timeout {probe.TimeoutSeconds.Value}s");
        }

        if (probe.FailureThreshold.HasValue)
        {
            timings.Add($"fail after {probe.FailureThreshold.Value}");
        }

        if (probe.SuccessThreshold.HasValue && probe.SuccessThreshold.Value > 1)
        {
            timings.Add($"success threshold {probe.SuccessThreshold.Value}");
        }

        return string.Join(
            " | ",
            new[] { handler }
                .Concat(timings)
                .Where(static part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string DescribeProbeHandler(V1Probe probe)
    {
        if (probe.HttpGet is not null)
        {
            return $"http GET {probe.HttpGet.Path ?? "/"} on {probe.HttpGet.Port}";
        }

        if (probe.TcpSocket is not null)
        {
            return $"tcp on {probe.TcpSocket.Port}";
        }

        if (probe.Exec is not null)
        {
            var command = probe.Exec.Command?.Where(static part => !string.IsNullOrWhiteSpace(part)).ToArray() ?? [];
            return command.Length is 0
                ? "exec"
                : $"exec {string.Join(" ", command)}";
        }

        if (probe.Grpc is not null)
        {
            return string.IsNullOrWhiteSpace(probe.Grpc.Service)
                ? $"grpc on {probe.Grpc.Port}"
                : $"grpc {probe.Grpc.Service} on {probe.Grpc.Port}";
        }

        return "configured";
    }

    private static string? DescribeVolume(V1Volume volume)
    {
        if (!string.IsNullOrWhiteSpace(volume.ConfigMap?.Name))
        {
            return $"ConfigMap/{volume.ConfigMap.Name}";
        }

        if (!string.IsNullOrWhiteSpace(volume.Secret?.SecretName))
        {
            return $"Secret/{volume.Secret.SecretName}";
        }

        if (volume.Projected?.Sources?.Count > 0)
        {
            return $"projected ({volume.Projected.Sources.Count} sources)";
        }

        if (volume.PersistentVolumeClaim is not null)
        {
            return $"PVC/{volume.PersistentVolumeClaim.ClaimName}";
        }

        if (volume.EmptyDir is not null)
        {
            return "emptyDir";
        }

        if (volume.DownwardAPI is not null)
        {
            return "Downward API";
        }

        if (volume.HostPath is not null)
        {
            return $"hostPath {volume.HostPath.Path}";
        }

        return "present";
    }

    private static string? DescribeAffinityPresence(bool hasRequiredRules, bool hasPreferredRules)
    {
        var parts = new List<string>();

        if (hasRequiredRules)
        {
            parts.Add("required");
        }

        if (hasPreferredRules)
        {
            parts.Add("preferred");
        }

        return parts.Count is 0
            ? null
            : string.Join(" + ", parts);
    }

    private static string? GetPodConditionSummary(IEnumerable<V1PodCondition>? conditions, string conditionType)
    {
        var condition = conditions?.FirstOrDefault(existing =>
            string.Equals(existing.Type, conditionType, StringComparison.OrdinalIgnoreCase));

        if (condition is null)
        {
            return null;
        }

        var parts = new List<string> { condition.Status };

        if (!string.IsNullOrWhiteSpace(condition.Reason))
        {
            parts.Add(condition.Reason);
        }

        return string.Join(" | ", parts.Where(static part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string? GetReadyContainerSummary(IEnumerable<V1ContainerStatus>? statuses)
    {
        if (statuses is null)
        {
            return null;
        }

        var materialized = statuses.ToArray();

        if (materialized.Length is 0)
        {
            return null;
        }

        var readyCount = materialized.Count(static status => status.Ready);
        return $"{readyCount}/{materialized.Length}";
    }

    private static string? GetCurrentContainerStateSummary(IEnumerable<V1ContainerStatus>? statuses, bool requireWaitingState)
    {
        if (statuses is null)
        {
            return null;
        }

        var summaries = statuses
            .Select(status =>
            {
                if (requireWaitingState && status.State?.Waiting is null)
                {
                    return null;
                }

                var stateSummary = DescribeContainerState(status.State);
                return string.IsNullOrWhiteSpace(stateSummary)
                    ? null
                    : $"{status.Name}: {stateSummary}";
            })
            .Where(static summary => !string.IsNullOrWhiteSpace(summary))
            .ToArray();

        return summaries.Length is 0
            ? null
            : string.Join(", ", summaries);
    }

    private static string? GetLastTerminationSummary(IEnumerable<V1ContainerStatus>? statuses)
    {
        if (statuses is null)
        {
            return null;
        }

        var summaries = statuses
            .Select(status =>
            {
                var terminated = status.LastState?.Terminated;

                if (terminated is null)
                {
                    return null;
                }

                var reason = string.IsNullOrWhiteSpace(terminated.Reason)
                    ? "terminated previously"
                    : terminated.Reason;

                return $"{status.Name}: {reason}";
            })
            .Where(static summary => !string.IsNullOrWhiteSpace(summary))
            .ToArray();

        return summaries.Length is 0
            ? null
            : string.Join(", ", summaries);
    }

    private static string? DescribeContainerState(V1ContainerState? state)
    {
        if (state?.Running is not null)
        {
            return "running";
        }

        if (state?.Waiting is not null)
        {
            return string.IsNullOrWhiteSpace(state.Waiting.Reason)
                ? "waiting"
                : $"waiting ({state.Waiting.Reason})";
        }

        if (state?.Terminated is not null)
        {
            return string.IsNullOrWhiteSpace(state.Terminated.Reason)
                ? "terminated"
                : $"terminated ({state.Terminated.Reason})";
        }

        return null;
    }

    private static IReadOnlyList<KubeRelatedResource> CreateOwnerRelations(IEnumerable<V1OwnerReference>? ownerReferences, string? namespaceName)
    {
        if (ownerReferences is null)
        {
            return [];
        }

        return ownerReferences
            .Where(ownerReference => !string.IsNullOrWhiteSpace(ownerReference.Name))
            .Select(ownerReference => new KubeRelatedResource(
                Relationship: "Owned by",
                Kind: TryMapKind(ownerReference.Kind),
                ApiVersion: ownerReference.ApiVersion ?? string.Empty,
                Name: ownerReference.Name,
                Namespace: namespaceName,
                Status: ownerReference.Controller is true ? "controller" : null,
                Summary: null))
            .ToArray();
    }

    private static KubeResourceKind? TryMapKind(string? kind)
    {
        return kind?.Trim() switch
        {
            nameof(KubeResourceKind.Namespace) => KubeResourceKind.Namespace,
            nameof(KubeResourceKind.Node) => KubeResourceKind.Node,
            nameof(KubeResourceKind.Pod) => KubeResourceKind.Pod,
            nameof(KubeResourceKind.Deployment) => KubeResourceKind.Deployment,
            nameof(KubeResourceKind.ReplicaSet) => KubeResourceKind.ReplicaSet,
            nameof(KubeResourceKind.StatefulSet) => KubeResourceKind.StatefulSet,
            nameof(KubeResourceKind.DaemonSet) => KubeResourceKind.DaemonSet,
            nameof(KubeResourceKind.Service) => KubeResourceKind.Service,
            nameof(KubeResourceKind.Ingress) => KubeResourceKind.Ingress,
            nameof(KubeResourceKind.ConfigMap) => KubeResourceKind.ConfigMap,
            nameof(KubeResourceKind.Secret) => KubeResourceKind.Secret,
            nameof(KubeResourceKind.Job) => KubeResourceKind.Job,
            nameof(KubeResourceKind.CronJob) => KubeResourceKind.CronJob,
            nameof(KubeResourceKind.Event) => KubeResourceKind.Event,
            _ => null
        };
    }

    private static KubeRelatedResource? CreateObjectReferenceRelation(string relationship, V1ObjectReference? reference)
    {
        if (reference is null || string.IsNullOrWhiteSpace(reference.Name))
        {
            return null;
        }

        return new KubeRelatedResource(
            Relationship: relationship,
            Kind: TryMapKind(reference.Kind),
            ApiVersion: reference.ApiVersion ?? string.Empty,
            Name: reference.Name,
            Namespace: reference.NamespaceProperty,
            Status: null,
            Summary: null);
    }

    private static JsonNode CreateRawManifestNode<T>(T item)
        where T : class, k8s.IKubernetesObject<V1ObjectMeta>
    {
        var node = JsonSerializer.SerializeToNode(item, RawManifestSerializerOptions);

        if (node is JsonObject rootObject)
        {
            if (item is V1Secret)
            {
                RedactSecretValues(rootObject);
            }
        }

        return node ?? new JsonObject();
    }

    private static void RedactSecretValues(JsonObject rootObject)
    {
        RedactSecretValues(rootObject as JsonNode);
    }

    private static void RedactSecretValues(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject jsonObject:
                foreach (var key in jsonObject.Select(static pair => pair.Key).ToArray())
                {
                    if (IsSecretValueContainerProperty(key) && jsonObject[key] is JsonObject targetObject)
                    {
                        RedactObjectValues(targetObject);
                        continue;
                    }

                    if (jsonObject[key] is JsonValue jsonValue &&
                        jsonValue.TryGetValue<string>(out var stringValue) &&
                        TryParseEmbeddedJson(stringValue) is { } embeddedJson)
                    {
                        RedactSecretValues(embeddedJson);
                        jsonObject[key] = embeddedJson.ToJsonString(EmbeddedJsonSerializerOptions);
                        continue;
                    }

                    RedactSecretValues(jsonObject[key]);
                }

                break;

            case JsonArray jsonArray:
                foreach (var item in jsonArray)
                {
                    RedactSecretValues(item);
                }

                break;
        }
    }

    private static void RedactObjectValues(JsonObject targetObject)
    {
        foreach (var key in targetObject.Select(static pair => pair.Key).ToArray())
        {
            targetObject[key] = "<redacted>";
        }
    }

    private static bool IsSecretValueContainerProperty(string propertyName)
    {
        return propertyName is "data" or "stringData";
    }

    private static JsonNode? TryParseEmbeddedJson(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            var parsed = JsonNode.Parse(value);
            return parsed is JsonObject or JsonArray
                ? parsed
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonSerializerOptions CreateRawManifestSerializerOptions()
    {
        return new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true
        };
    }

    private static JsonSerializerOptions CreateEmbeddedJsonSerializerOptions()
    {
        return new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    private static IReadOnlyList<KubeResourceDetailSection> BuildCustomResourceSections(
        string contextName,
        KubeCustomResourceType customResourceType,
        KubeResourceSummary summary,
        JsonObject? metadata,
        JsonObject? specObject,
        JsonObject? statusObject)
    {
        var sections = new List<KubeResourceDetailSection>
        {
            CreateSection(
                "Metadata",
                Field("Context", contextName),
                Field("Kind", customResourceType.Kind),
                Field("API version", summary.ApiVersion),
                Field("Namespace", summary.Namespace ?? "cluster-scoped"),
                Field("UID", summary.Uid),
                Field("Created", summary.CreatedAtUtc?.ToLocalTime().ToString("u", CultureInfo.InvariantCulture))),
            CreateSection(
                "Definition",
                Field("Group", customResourceType.Group),
                Field("Version", customResourceType.Version),
                Field("Plural", customResourceType.Plural),
                Field("Scope", customResourceType.ScopeLabel),
                Field("List kind", customResourceType.ListKind),
                Field("Singular", customResourceType.Singular)),
            CreateSection(
                "Overview",
                Field("Status", summary.Status),
                Field("Summary", summary.Summary),
                Field("Generation", metadata?["generation"]?.GetValue<long>()),
                Field("Resource version", metadata?["resourceVersion"]?.GetValue<string>()),
                Field("Finalizers", (metadata?["finalizers"] as JsonArray)?.Count),
                Field("Spec fields", specObject?.Count),
                Field("Status fields", statusObject?.Count))
        };

        var conditionsSection = CreateJsonConditionsSection(statusObject?["conditions"] as JsonArray);
        var statusSection = CreateJsonScalarSection("Status", statusObject, ["conditions"]);
        var specSection = CreateJsonScalarSection("Spec", specObject);
        var labelsSection = CreateJsonNamedValueSection("Labels", metadata?["labels"] as JsonObject);
        var annotationsSection = CreateJsonNamedValueSection("Annotations", metadata?["annotations"] as JsonObject);

        if (conditionsSection is not null)
        {
            sections.Add(conditionsSection);
        }

        if (statusSection is not null)
        {
            sections.Add(statusSection);
        }

        if (specSection is not null)
        {
            sections.Add(specSection);
        }

        if (labelsSection is not null)
        {
            sections.Add(labelsSection);
        }

        if (annotationsSection is not null)
        {
            sections.Add(annotationsSection);
        }

        return sections;
    }

    private static KubeResourceDetailSection? CreateJsonScalarSection(
        string title,
        JsonObject? source,
        IReadOnlyList<string>? excludeKeys = null)
    {
        if (source is null)
        {
            return null;
        }

        var excluded = excludeKeys ?? [];
        var fields = source
            .Where(property => property.Value is JsonValue && !excluded.Contains(property.Key, StringComparer.OrdinalIgnoreCase))
            .Select(property => new KubeResourceDetailField(property.Key, property.Value!.ToString()))
            .ToArray();

        return fields.Length is 0
            ? null
            : new KubeResourceDetailSection(title, fields);
    }

    private static KubeResourceDetailSection? CreateJsonNamedValueSection(string title, JsonObject? source)
    {
        if (source is null)
        {
            return null;
        }

        var fields = source
            .Select(property => new KubeResourceDetailField(property.Key, property.Value?.ToString() ?? string.Empty))
            .ToArray();

        return fields.Length is 0
            ? null
            : new KubeResourceDetailSection(title, fields);
    }

    private static KubeResourceDetailSection? CreateJsonConditionsSection(JsonArray? conditions)
    {
        if (conditions is null)
        {
            return null;
        }

        var fields = conditions
            .OfType<JsonObject>()
            .Select(static condition =>
            {
                var type = condition["type"]?.GetValue<string>();
                var status = condition["status"]?.GetValue<string>();
                var reason = condition["reason"]?.GetValue<string>();
                var message = condition["message"]?.GetValue<string>();
                var parts = new[] { status, reason, message }
                    .Where(static part => !string.IsNullOrWhiteSpace(part))
                    .ToArray();

                return string.IsNullOrWhiteSpace(type) || parts.Length is 0
                    ? null
                    : new KubeResourceDetailField(type, string.Join(" | ", parts));
            })
            .Where(static field => field is not null)
            .Cast<KubeResourceDetailField>()
            .ToArray();

        return fields.Length is 0
            ? null
            : new KubeResourceDetailSection("Conditions", fields);
    }

    private static IReadOnlyList<KubeRelatedResource> CreateOwnerRelationsFromJson(JsonArray? ownerReferences, string? namespaceName)
    {
        if (ownerReferences is null)
        {
            return [];
        }

        return ownerReferences
            .OfType<JsonObject>()
            .Where(static ownerReference => !string.IsNullOrWhiteSpace(ownerReference["name"]?.GetValue<string>()))
            .Select(ownerReference => new KubeRelatedResource(
                Relationship: "Owned by",
                Kind: TryMapKind(ownerReference["kind"]?.GetValue<string>()),
                ApiVersion: ownerReference["apiVersion"]?.GetValue<string>() ?? string.Empty,
                Name: ownerReference["name"]?.GetValue<string>() ?? string.Empty,
                Namespace: namespaceName,
                Status: ownerReference["controller"]?.GetValue<bool>() is true ? "controller" : null,
                Summary: null))
            .ToArray();
    }
}
