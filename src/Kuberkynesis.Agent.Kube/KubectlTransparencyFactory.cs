using System.Text;
using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Kube;

internal static class KubectlTransparencyFactory
{
    private static readonly IReadOnlyList<KubeResourceKind> NamespacedQueryKinds =
    [
        KubeResourceKind.Pod,
        KubeResourceKind.Deployment,
        KubeResourceKind.ReplicaSet,
        KubeResourceKind.StatefulSet,
        KubeResourceKind.DaemonSet,
        KubeResourceKind.Service,
        KubeResourceKind.Ingress,
        KubeResourceKind.ConfigMap,
        KubeResourceKind.Secret,
        KubeResourceKind.Job,
        KubeResourceKind.CronJob,
        KubeResourceKind.Event
    ];

    private static readonly IReadOnlyList<KubeResourceKind> ClusterScopedQueryKinds =
    [
        KubeResourceKind.Namespace,
        KubeResourceKind.Node
    ];

    public static IReadOnlyList<KubectlCommandPreview> CreateForQuery(
        KubeResourceQueryRequest request,
        IReadOnlyList<string> targetContexts,
        int limitApplied)
    {
        var notes = BuildQueryNotes(request, limitApplied);

        if (request.IncludeAllSupportedKinds)
        {
            return CreateForMixedQuery(targetContexts, request.Namespace, notes);
        }

        return targetContexts
            .Select(contextName => CreateCommandPreview(
                label: targetContexts.Count > 1 ? $"Query in {contextName}" : "Resource query",
                command: BuildGetCommand(
                    contextName,
                    request.Kind,
                    request.CustomResourceType,
                    request.Namespace,
                    resourceName: null,
                    output: "wide"),
                transparencyKind: KubectlTransparencyKind.Equivalent,
                targetSummary: $"{GetDisplayKindName(request.Kind, request.CustomResourceType)} query",
                scopeSummary: BuildScopeSummary(contextName, request.Namespace, IsNamespaced(request.Kind, request.CustomResourceType)),
                notes: notes))
            .ToArray();
    }

    public static IReadOnlyList<KubectlCommandPreview> CreateForDetail(KubeResourceDetailRequest request)
    {
        return
        [
            CreateCommandPreview(
                label: "Inspector detail",
                command: BuildDescribeCommand(request.ContextName, request.Kind, request.CustomResourceType, request.Namespace, request.Name),
                transparencyKind: KubectlTransparencyKind.Equivalent,
                targetSummary: BuildResourceTargetSummary(request.Kind, request.CustomResourceType, request.Name),
                scopeSummary: BuildScopeSummary(request.ContextName, request.Namespace, IsNamespaced(request.Kind, request.CustomResourceType)),
                notes: request.Kind is KubeResourceKind.Secret
                    ? "Secret values stay hidden in the UI; this preview sticks to metadata-safe inspection and there is no reveal path here."
                    : null),
            CreateCommandPreview(
                label: "Raw manifest",
                command: BuildGetCommand(request.ContextName, request.Kind, request.CustomResourceType, request.Namespace, request.Name, output: "json"),
                transparencyKind: KubectlTransparencyKind.Equivalent,
                targetSummary: BuildResourceTargetSummary(request.Kind, request.CustomResourceType, request.Name),
                scopeSummary: BuildScopeSummary(request.ContextName, request.Namespace, IsNamespaced(request.Kind, request.CustomResourceType)),
                notes: request.Kind is KubeResourceKind.Secret
                    ? "kubectl would return base64-encoded secret values here. The UI redacts them in the raw inspector and does not provide a reveal toggle."
                    : "Matches the raw JSON view in the inspector.")
        ];
    }

    public static IReadOnlyList<KubectlCommandPreview> CreateForGraph(KubeResourceDetailRequest request)
    {
        return
        [
            CreateCommandPreview(
                label: "Graph seed resource",
                command: BuildDescribeCommand(request.ContextName, request.Kind, request.CustomResourceType, request.Namespace, request.Name),
                transparencyKind: KubectlTransparencyKind.Approximate,
                targetSummary: BuildResourceTargetSummary(request.Kind, request.CustomResourceType, request.Name),
                scopeSummary: BuildScopeSummary(request.ContextName, request.Namespace, IsNamespaced(request.Kind, request.CustomResourceType)),
                notes: "The dependency graph starts from this resource and expands nearby modeled relationships such as owners, selectors, ingress backends, scheduled nodes, and spawned jobs."),
            CreateCommandPreview(
                label: "Relationship source manifest",
                command: BuildGetCommand(request.ContextName, request.Kind, request.CustomResourceType, request.Namespace, request.Name, output: "json"),
                transparencyKind: KubectlTransparencyKind.Approximate,
                targetSummary: BuildResourceTargetSummary(request.Kind, request.CustomResourceType, request.Name),
                scopeSummary: BuildScopeSummary(request.ContextName, request.Namespace, IsNamespaced(request.Kind, request.CustomResourceType)),
                notes: "kubectl has no native dependency graph output here. The UI infers edges from the manifest and neighboring lookups.")
        ];
    }

    public static IReadOnlyList<KubectlCommandPreview> CreateForTimeline(KubeResourceDetailRequest request)
    {
        return
        [
            CreateCommandPreview(
                label: "Selected resource events",
                command: BuildEventsCommand(request.ContextName, request.Kind, request.Namespace, request.Name),
                transparencyKind: KubectlTransparencyKind.Equivalent,
                targetSummary: BuildResourceTargetSummary(request.Kind, request.CustomResourceType, request.Name),
                scopeSummary: BuildScopeSummary(request.ContextName, request.Namespace, IsNamespaced(request.Kind, request.CustomResourceType)),
                notes: "The timeline merges these events with nearby related-resource events when they are modeled and available.")
        ];
    }

    public static IReadOnlyList<KubectlCommandPreview> CreateForLiveSurface(KubeResourceDetailRequest request)
    {
        return
        [
            CreateCommandPreview(
                label: "Live surface v1",
                command: BuildEventsCommand(request.ContextName, request.Kind, request.Namespace, request.Name),
                transparencyKind: KubectlTransparencyKind.Approximate,
                targetSummary: BuildResourceTargetSummary(request.Kind, request.CustomResourceType, request.Name),
                scopeSummary: BuildScopeSummary(request.ContextName, request.Namespace, IsNamespaced(request.Kind, request.CustomResourceType)),
                notes: "Live Surface v1 uses Kubernetes Event resources as the upstream cluster feed, then derives local status, activity, and likely-cause observations for the same scope. App-defined event schemas are still future work.")
        ];
    }

    public static IReadOnlyList<KubectlCommandPreview> CreateForPodLogs(
        KubePodLogRequest request,
        string resolvedContainerName,
        bool containerWasAutoSelected)
    {
        return
        [
            CreateCommandPreview(
                label: "Pod logs",
                command: BuildLogsCommand(request.ContextName, request.Namespace, request.PodName, resolvedContainerName, request.TailLines),
                transparencyKind: KubectlTransparencyKind.Equivalent,
                targetSummary: $"{KubeResourceKind.Pod}/{request.PodName}",
                scopeSummary: BuildScopeSummary(request.ContextName, request.Namespace, namespaced: true),
                notes: containerWasAutoSelected
                    ? $"Container '{resolvedContainerName}' was auto-selected from the pod spec."
                    : null)
        ];
    }

    public static IReadOnlyList<KubectlCommandPreview> CreateForPodExec(
        string contextName,
        string namespaceName,
        string podName,
        string? containerName,
        IReadOnlyList<string> command)
    {
        return
        [
            CreateCommandPreview(
                label: "Pod exec shell",
                command: BuildExecCommand(contextName, namespaceName, podName, containerName, command),
                transparencyKind: KubectlTransparencyKind.Equivalent,
                targetSummary: $"{KubeResourceKind.Pod}/{podName}",
                scopeSummary: BuildScopeSummary(contextName, namespaceName, namespaced: true),
                notes: "The browser opens this shell only after an explicit user action, and preview-only origins cannot start or attach to exec sessions.")
        ];
    }

    public static IReadOnlyList<KubectlCommandPreview> CreateForResourceMetrics(
        KubeResourceKind kind,
        string contextName,
        string? namespaceName,
        string resourceName,
        IReadOnlyList<string> contributingPodNames)
    {
        if (kind is KubeResourceKind.Node)
        {
            return
            [
                CreateCommandPreview(
                    label: "Node usage",
                    command: BuildTopNodeCommand(contextName, resourceName),
                    transparencyKind: KubectlTransparencyKind.Equivalent,
                    targetSummary: $"{KubeResourceKind.Node}/{resourceName}",
                    scopeSummary: BuildScopeSummary(contextName, namespaceName: null, namespaced: false),
                    notes: "Shows the current node CPU and memory usage from the metrics API.")
            ];
        }

        if (kind is KubeResourceKind.Pod && !string.IsNullOrWhiteSpace(namespaceName))
        {
            return
            [
                CreateCommandPreview(
                    label: "Pod usage",
                    command: BuildTopPodCommand(contextName, namespaceName, [resourceName]),
                    transparencyKind: KubectlTransparencyKind.Equivalent,
                    targetSummary: $"{KubeResourceKind.Pod}/{resourceName}",
                    scopeSummary: BuildScopeSummary(contextName, namespaceName, namespaced: true),
                    notes: "Use --containers in a terminal if you want per-container usage detail.")
            ];
        }

        if (!string.IsNullOrWhiteSpace(namespaceName) && contributingPodNames.Count > 0)
        {
            return
            [
                CreateCommandPreview(
                    label: "Contributing pod usage",
                    command: BuildTopPodCommand(contextName, namespaceName, contributingPodNames),
                    transparencyKind: KubectlTransparencyKind.Approximate,
                    targetSummary: $"{contributingPodNames.Count} contributing pod(s)",
                    scopeSummary: BuildScopeSummary(contextName, namespaceName, namespaced: true),
                    notes: "The UI aggregates the contributing pod metrics to show calm workload-level usage.")
            ];
        }

        return [];
    }

    public static IReadOnlyList<KubectlCommandPreview> CreateForPodMetricsQuery(IReadOnlyList<KubeResourceIdentity> targets)
    {
        return targets
            .Where(static target => !string.IsNullOrWhiteSpace(target.Namespace))
            .GroupBy(static target => new
            {
                target.ContextName,
                Namespace = target.Namespace!
            })
            .OrderBy(static group => group.Key.ContextName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static group => group.Key.Namespace, StringComparer.OrdinalIgnoreCase)
            .Select(group => CreateCommandPreview(
                label: $"{group.Key.ContextName} / {group.Key.Namespace}",
                command: BuildTopPodCommand(group.Key.ContextName, group.Key.Namespace, group.Select(static target => target.Name).ToArray()),
                transparencyKind: KubectlTransparencyKind.Approximate,
                targetSummary: $"{group.Count()} pod(s)",
                scopeSummary: BuildScopeSummary(group.Key.ContextName, group.Key.Namespace, namespaced: true),
                notes: "These are the nearest kubectl equivalents for the current group pod usage snapshot."))
            .ToArray();
    }

    public static IReadOnlyList<KubectlCommandPreview> CreateForActionPreview(KubeActionPreviewRequest request)
    {
        return request.Action switch
        {
            KubeActionKind.ScaleDeployment => CreateForScaleDeploymentPreview(request),
            KubeActionKind.RestartDeploymentRollout => CreateForRestartDeploymentPreview(request),
            KubeActionKind.RollbackDeploymentRollout => CreateForRollbackDeploymentPreview(request),
            KubeActionKind.DeletePod => CreateForDeletePodPreview(request),
            KubeActionKind.ScaleStatefulSet => CreateForScaleStatefulSetPreview(request),
            KubeActionKind.RestartDaemonSetRollout => CreateForRestartDaemonSetPreview(request),
            KubeActionKind.DeleteJob => CreateForDeleteJobPreview(request),
            KubeActionKind.SuspendCronJob => CreateForCronJobSuspendPreview(request, suspend: true),
            KubeActionKind.ResumeCronJob => CreateForCronJobSuspendPreview(request, suspend: false),
            KubeActionKind.CordonNode => CreateForNodeSchedulingPreview(request, cordon: true),
            KubeActionKind.UncordonNode => CreateForNodeSchedulingPreview(request, cordon: false),
            KubeActionKind.DrainNode => CreateForDrainNodePreview(request),
            _ => []
        };
    }

    private static string? BuildQueryNotes(KubeResourceQueryRequest request, int limitApplied)
    {
        var notes = new List<string>();

        if (request.IncludeAllSupportedKinds)
        {
            notes.Add("All supported kinds fans out across typed resource queries instead of one literal kubectl call.");
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            notes.Add($"Search '{request.Search.Trim()}' is applied in the UI after retrieval.");
        }

        notes.Add($"UI result cap: {limitApplied}.");

        return notes.Count is 0
            ? null
            : string.Join(" ", notes);
    }

    private static IReadOnlyList<KubectlCommandPreview> CreateForMixedQuery(
        IReadOnlyList<string> targetContexts,
        string? namespaceName,
        string? notes)
    {
        var commands = new List<KubectlCommandPreview>();

        foreach (var contextName in targetContexts)
        {
            if (string.IsNullOrWhiteSpace(namespaceName))
            {
                commands.Add(CreateCommandPreview(
                    label: targetContexts.Count > 1 ? $"Cluster-scoped kinds in {contextName}" : "Cluster-scoped kinds",
                    command: BuildMixedGetCommand(contextName, namespaceName: null, ClusterScopedQueryKinds, output: "wide"),
                    transparencyKind: KubectlTransparencyKind.Approximate,
                    targetSummary: "All supported cluster kinds",
                    scopeSummary: BuildScopeSummary(contextName, namespaceName: null, namespaced: false),
                    notes: notes));
            }

            commands.Add(CreateCommandPreview(
                label: targetContexts.Count > 1 ? $"Namespaced kinds in {contextName}" : "Namespaced kinds",
                command: BuildMixedGetCommand(contextName, namespaceName, NamespacedQueryKinds, output: "wide"),
                transparencyKind: KubectlTransparencyKind.Approximate,
                targetSummary: "All supported namespaced kinds",
                scopeSummary: BuildScopeSummary(contextName, namespaceName, namespaced: true),
                notes: notes));
        }

        return commands;
    }

    private static IReadOnlyList<KubectlCommandPreview> CreateForScaleDeploymentPreview(KubeActionPreviewRequest request)
    {
        var targetReplicas = request.TargetReplicas ?? 0;

        return
        [
            CreateCommandPreview(
                label: "Scale preview",
                command: BuildScaleDeploymentCommand(request.ContextName, request.Namespace!, request.Name, targetReplicas),
                transparencyKind: KubectlTransparencyKind.Equivalent,
                targetSummary: BuildResourceTargetSummary(request.Kind, customResourceType: null, request.Name),
                scopeSummary: BuildScopeSummary(request.ContextName, request.Namespace, namespaced: true),
                notes: "This preview does not execute the command. It compares the requested replica count with the current deployment status and matching pod set.",
                isDryRun: true,
                requestSummary: $"replicas={targetReplicas}")
        ];
    }

    private static IReadOnlyList<KubectlCommandPreview> CreateForRestartDeploymentPreview(KubeActionPreviewRequest request)
    {
        return
        [
            CreateCommandPreview(
                label: "Rollout restart preview",
                command: BuildRolloutRestartCommand(request.ContextName, request.Namespace!, request.Name),
                transparencyKind: KubectlTransparencyKind.Equivalent,
                targetSummary: BuildResourceTargetSummary(request.Kind, customResourceType: null, request.Name),
                scopeSummary: BuildScopeSummary(request.ContextName, request.Namespace, namespaced: true),
                notes: "This preview does not execute the command. It describes the current deployment state that a rollout restart would act on.",
                isDryRun: true,
                requestSummary: "spec.template.metadata.annotations.kubectl.kubernetes.io/restartedAt=<preview time>")
        ];
    }

    private static IReadOnlyList<KubectlCommandPreview> CreateForRollbackDeploymentPreview(KubeActionPreviewRequest request)
    {
        return
        [
            CreateCommandPreview(
                label: "Rollout undo preview",
                command: BuildRolloutUndoCommand(request.ContextName, request.Namespace!, request.Name, revision: null),
                transparencyKind: KubectlTransparencyKind.Equivalent,
                targetSummary: BuildResourceTargetSummary(request.Kind, customResourceType: null, request.Name),
                scopeSummary: BuildScopeSummary(request.ContextName, request.Namespace, namespaced: true),
                notes: "This preview does not execute the command. It describes the retained ReplicaSet history that a rollout undo would try to restore.",
                isDryRun: true,
                requestSummary: "rollbackToRevision=<retained prior revision>")
        ];
    }

    private static IReadOnlyList<KubectlCommandPreview> CreateForDeletePodPreview(KubeActionPreviewRequest request)
    {
        return
        [
            CreateCommandPreview(
                label: "Delete pod preview",
                command: BuildDeletePodCommand(request.ContextName, request.Namespace!, request.Name),
                transparencyKind: KubectlTransparencyKind.Equivalent,
                targetSummary: BuildResourceTargetSummary(request.Kind, customResourceType: null, request.Name),
                scopeSummary: BuildScopeSummary(request.ContextName, request.Namespace, namespaced: true),
                notes: "This preview does not execute the command. It describes the current pod owner and whether another controller is likely to recreate the pod.",
                isDryRun: true)
        ];
    }

    private static IReadOnlyList<KubectlCommandPreview> CreateForScaleStatefulSetPreview(KubeActionPreviewRequest request)
    {
        var targetReplicas = request.TargetReplicas ?? 0;

        return
        [
            CreateCommandPreview(
                label: "StatefulSet scale preview",
                command: BuildScaleStatefulSetCommand(request.ContextName, request.Namespace!, request.Name, targetReplicas),
                transparencyKind: KubectlTransparencyKind.Equivalent,
                targetSummary: BuildResourceTargetSummary(request.Kind, customResourceType: null, request.Name),
                scopeSummary: BuildScopeSummary(request.ContextName, request.Namespace, namespaced: true),
                notes: "This preview does not execute the command. It compares the requested statefulset replica count with the current status and ordinal pod set.",
                isDryRun: true,
                requestSummary: $"replicas={targetReplicas}")
        ];
    }

    private static IReadOnlyList<KubectlCommandPreview> CreateForRestartDaemonSetPreview(KubeActionPreviewRequest request)
    {
        return
        [
            CreateCommandPreview(
                label: "DaemonSet rollout restart preview",
                command: BuildRolloutRestartDaemonSetCommand(request.ContextName, request.Namespace!, request.Name),
                transparencyKind: KubectlTransparencyKind.Equivalent,
                targetSummary: BuildResourceTargetSummary(request.Kind, customResourceType: null, request.Name),
                scopeSummary: BuildScopeSummary(request.ContextName, request.Namespace, namespaced: true),
                notes: "This preview does not execute the command. It describes the current daemonset state that a restart would rotate.",
                isDryRun: true,
                requestSummary: "spec.template.metadata.annotations.kubectl.kubernetes.io/restartedAt=<preview time>")
        ];
    }

    private static IReadOnlyList<KubectlCommandPreview> CreateForDeleteJobPreview(KubeActionPreviewRequest request)
    {
        return
        [
            CreateCommandPreview(
                label: "Delete job preview",
                command: BuildDeleteJobCommand(request.ContextName, request.Namespace!, request.Name),
                transparencyKind: KubectlTransparencyKind.Equivalent,
                targetSummary: BuildResourceTargetSummary(request.Kind, customResourceType: null, request.Name),
                scopeSummary: BuildScopeSummary(request.ContextName, request.Namespace, namespaced: true),
                notes: "This preview does not execute the command. It describes the current job state that deletion would remove.",
                isDryRun: true)
        ];
    }

    private static IReadOnlyList<KubectlCommandPreview> CreateForCronJobSuspendPreview(KubeActionPreviewRequest request, bool suspend)
    {
        return
        [
            CreateCommandPreview(
                label: suspend ? "Suspend cronjob preview" : "Resume cronjob preview",
                command: BuildPatchCronJobSuspendCommand(request.ContextName, request.Namespace!, request.Name, suspend),
                transparencyKind: KubectlTransparencyKind.Equivalent,
                targetSummary: BuildResourceTargetSummary(request.Kind, customResourceType: null, request.Name),
                scopeSummary: BuildScopeSummary(request.ContextName, request.Namespace, namespaced: true),
                notes: "This preview does not execute the command. It shows the CronJob schedule state that the patch would change.",
                isDryRun: true,
                requestSummary: $"spec.suspend={suspend.ToString().ToLowerInvariant()}")
        ];
    }

    private static IReadOnlyList<KubectlCommandPreview> CreateForNodeSchedulingPreview(KubeActionPreviewRequest request, bool cordon)
    {
        return
        [
            CreateCommandPreview(
                label: cordon ? "Cordon node preview" : "Uncordon node preview",
                command: cordon
                    ? BuildCordonNodeCommand(request.ContextName, request.Name)
                    : BuildUncordonNodeCommand(request.ContextName, request.Name),
                transparencyKind: KubectlTransparencyKind.Equivalent,
                targetSummary: BuildResourceTargetSummary(request.Kind, customResourceType: null, request.Name),
                scopeSummary: BuildScopeSummary(request.ContextName, namespaceName: null, namespaced: false),
                notes: "This preview does not execute the command. It shows the node scheduling change that the typed-client path would request.",
                isDryRun: true,
                requestSummary: cordon ? "spec.unschedulable=true" : "spec.unschedulable=false")
        ];
    }

    private static IReadOnlyList<KubectlCommandPreview> CreateForDrainNodePreview(KubeActionPreviewRequest request)
    {
        return
        [
            CreateCommandPreview(
                label: "Drain node preview",
                command: BuildDrainNodeCommand(request.ContextName, request.Name),
                transparencyKind: KubectlTransparencyKind.Approximate,
                targetSummary: BuildResourceTargetSummary(request.Kind, customResourceType: null, request.Name),
                scopeSummary: BuildScopeSummary(request.ContextName, namespaceName: null, namespaced: false),
                notes: "This preview stays read-only in the current slice. It shows the closest kubectl shape for the eventual drain path.",
                isDryRun: true,
                requestSummary: "--ignore-daemonsets --delete-emptydir-data")
        ];
    }

    private static KubectlCommandPreview CreateCommandPreview(
        string label,
        string command,
        KubectlTransparencyKind transparencyKind,
        string? targetSummary,
        string? scopeSummary,
        string? notes = null,
        bool isDryRun = false,
        string? requestSummary = null)
    {
        return new KubectlCommandPreview(
            Label: label,
            Command: command,
            Notes: notes,
            TransparencyKind: transparencyKind,
            TargetSummary: targetSummary,
            ScopeSummary: scopeSummary,
            IsDryRun: isDryRun,
            RequestSummary: requestSummary);
    }

    private static string BuildResourceTargetSummary(
        KubeResourceKind kind,
        KubeCustomResourceType? customResourceType,
        string resourceName)
    {
        return $"{GetDisplayKindName(kind, customResourceType)}/{resourceName.Trim()}";
    }

    private static string BuildScopeSummary(string contextName, string? namespaceName, bool namespaced)
    {
        if (!namespaced)
        {
            return $"{contextName} / cluster-scoped";
        }

        if (string.IsNullOrWhiteSpace(namespaceName))
        {
            return $"{contextName} / all namespaces";
        }

        return $"{contextName} / {namespaceName.Trim()}";
    }

    private static string BuildGetCommand(
        string contextName,
        KubeResourceKind kind,
        KubeCustomResourceType? customResourceType,
        string? namespaceName,
        string? resourceName,
        string output)
    {
        var builder = new StringBuilder("kubectl");
        builder.Append(" --context ").Append(EscapeArgument(contextName));

        if (IsNamespaced(kind, customResourceType))
        {
            if (string.IsNullOrWhiteSpace(namespaceName))
            {
                builder.Append(" -A");
            }
            else
            {
                builder.Append(" -n ").Append(EscapeArgument(namespaceName.Trim()));
            }
        }

        builder.Append(" get ").Append(GetKubectlResourceName(kind, customResourceType));

        if (!string.IsNullOrWhiteSpace(resourceName))
        {
            builder.Append(' ').Append(EscapeArgument(resourceName.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(output))
        {
            builder.Append(" -o ").Append(output);
        }

        return builder.ToString();
    }

    private static string BuildMixedGetCommand(
        string contextName,
        string? namespaceName,
        IReadOnlyList<KubeResourceKind> kinds,
        string output)
    {
        var builder = new StringBuilder("kubectl");
        builder.Append(" --context ").Append(EscapeArgument(contextName));

        if (kinds.Any(IsNamespaced))
        {
            if (string.IsNullOrWhiteSpace(namespaceName))
            {
                builder.Append(" -A");
            }
            else
            {
                builder.Append(" -n ").Append(EscapeArgument(namespaceName.Trim()));
            }
        }

        builder.Append(" get ").Append(string.Join(',', kinds.Select(kind => GetKubectlResourceName(kind, customResourceType: null))));

        if (!string.IsNullOrWhiteSpace(output))
        {
            builder.Append(" -o ").Append(output);
        }

        return builder.ToString();
    }

    private static string BuildRolloutRestartCommand(string contextName, string namespaceName, string resourceName)
    {
        var builder = new StringBuilder("kubectl");
        builder.Append(" --context ").Append(EscapeArgument(contextName));
        builder.Append(" -n ").Append(EscapeArgument(namespaceName));
        builder.Append(" rollout restart deployment/").Append(EscapeArgument(resourceName));
        return builder.ToString();
    }

    private static string BuildRolloutUndoCommand(string contextName, string namespaceName, string resourceName, int? revision)
    {
        var builder = new StringBuilder("kubectl");
        builder.Append(" --context ").Append(EscapeArgument(contextName));
        builder.Append(" -n ").Append(EscapeArgument(namespaceName));
        builder.Append(" rollout undo deployment/").Append(EscapeArgument(resourceName));

        if (revision.HasValue)
        {
            builder.Append(" --to-revision=").Append(revision.Value);
        }

        return builder.ToString();
    }

    private static string BuildDeletePodCommand(string contextName, string namespaceName, string resourceName)
    {
        var builder = new StringBuilder("kubectl");
        builder.Append(" --context ").Append(EscapeArgument(contextName));
        builder.Append(" -n ").Append(EscapeArgument(namespaceName));
        builder.Append(" delete pod/").Append(EscapeArgument(resourceName));
        return builder.ToString();
    }

    private static string BuildDeleteJobCommand(string contextName, string namespaceName, string resourceName)
    {
        var builder = new StringBuilder("kubectl");
        builder.Append(" --context ").Append(EscapeArgument(contextName));
        builder.Append(" -n ").Append(EscapeArgument(namespaceName));
        builder.Append(" delete job/").Append(EscapeArgument(resourceName));
        return builder.ToString();
    }

    private static string BuildDescribeCommand(
        string contextName,
        KubeResourceKind kind,
        KubeCustomResourceType? customResourceType,
        string? namespaceName,
        string resourceName)
    {
        if (kind is KubeResourceKind.Secret)
        {
            var builder = new StringBuilder("kubectl");
            builder.Append(" --context ").Append(EscapeArgument(contextName));
            builder.Append(" -n ").Append(EscapeArgument(namespaceName!.Trim()));
            builder.Append(" describe ").Append(GetKubectlResourceName(kind, customResourceType)).Append(' ').Append(EscapeArgument(resourceName));
            return builder.ToString();
        }

        var describeCommand = BuildGetCommand(contextName, kind, customResourceType, namespaceName, resourceName, output: string.Empty);
        return describeCommand.Replace(" get ", " describe ", StringComparison.Ordinal);
    }

    private static string BuildLogsCommand(
        string contextName,
        string namespaceName,
        string podName,
        string containerName,
        int tailLines)
    {
        var builder = new StringBuilder("kubectl");
        builder.Append(" --context ").Append(EscapeArgument(contextName));
        builder.Append(" -n ").Append(EscapeArgument(namespaceName));
        builder.Append(" logs pod/").Append(EscapeArgument(podName));
        builder.Append(" -c ").Append(EscapeArgument(containerName));
        builder.Append(" --tail=").Append(tailLines);
        builder.Append(" --timestamps");
        return builder.ToString();
    }

    private static string BuildEventsCommand(
        string contextName,
        KubeResourceKind kind,
        string? namespaceName,
        string resourceName)
    {
        var builder = new StringBuilder("kubectl");
        builder.Append(" --context ").Append(EscapeArgument(contextName));

        if (IsNamespaced(kind))
        {
            builder.Append(" -n ").Append(EscapeArgument(namespaceName!.Trim()));
        }
        else
        {
            builder.Append(" -A");
        }

        builder.Append(" get events");
        builder.Append(" --field-selector involvedObject.kind=").Append(GetEventKindName(kind));
        builder.Append(",involvedObject.name=").Append(EscapeArgument(resourceName));
        builder.Append(" --sort-by=.lastTimestamp");
        return builder.ToString();
    }

    private static string BuildTopNodeCommand(string contextName, string resourceName)
    {
        var builder = new StringBuilder("kubectl");
        builder.Append(" --context ").Append(EscapeArgument(contextName));
        builder.Append(" top node ").Append(EscapeArgument(resourceName));
        return builder.ToString();
    }

    private static string BuildTopPodCommand(string contextName, string namespaceName, IReadOnlyList<string> podNames)
    {
        var builder = new StringBuilder("kubectl");
        builder.Append(" --context ").Append(EscapeArgument(contextName));
        builder.Append(" -n ").Append(EscapeArgument(namespaceName));
        builder.Append(" top pod");

        foreach (var podName in podNames
                     .Where(static podName => !string.IsNullOrWhiteSpace(podName))
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(static podName => podName, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(' ').Append(EscapeArgument(podName));
        }

        return builder.ToString();
    }

    private static string BuildExecCommand(
        string contextName,
        string namespaceName,
        string podName,
        string? containerName,
        IReadOnlyList<string> command)
    {
        var builder = new StringBuilder("kubectl");
        builder.Append(" --context ").Append(EscapeArgument(contextName));
        builder.Append(" -n ").Append(EscapeArgument(namespaceName));
        builder.Append(" exec -i ").Append(EscapeArgument(podName));

        if (!string.IsNullOrWhiteSpace(containerName))
        {
            builder.Append(" -c ").Append(EscapeArgument(containerName.Trim()));
        }

        builder.Append(" --");

        foreach (var part in command.Where(static part => !string.IsNullOrWhiteSpace(part)))
        {
            builder.Append(' ').Append(EscapeArgument(part.Trim()));
        }

        return builder.ToString();
    }

    private static string BuildScaleDeploymentCommand(string contextName, string namespaceName, string resourceName, int targetReplicas)
    {
        var builder = new StringBuilder("kubectl");
        builder.Append(" --context ").Append(EscapeArgument(contextName));
        builder.Append(" -n ").Append(EscapeArgument(namespaceName));
        builder.Append(" scale deployment/").Append(EscapeArgument(resourceName));
        builder.Append(" --replicas=").Append(targetReplicas);
        return builder.ToString();
    }

    private static string BuildScaleStatefulSetCommand(string contextName, string namespaceName, string resourceName, int targetReplicas)
    {
        var builder = new StringBuilder("kubectl");
        builder.Append(" --context ").Append(EscapeArgument(contextName));
        builder.Append(" -n ").Append(EscapeArgument(namespaceName));
        builder.Append(" scale statefulset/").Append(EscapeArgument(resourceName));
        builder.Append(" --replicas=").Append(targetReplicas);
        return builder.ToString();
    }

    private static string BuildRolloutRestartDaemonSetCommand(string contextName, string namespaceName, string resourceName)
    {
        var builder = new StringBuilder("kubectl");
        builder.Append(" --context ").Append(EscapeArgument(contextName));
        builder.Append(" -n ").Append(EscapeArgument(namespaceName));
        builder.Append(" rollout restart daemonset/").Append(EscapeArgument(resourceName));
        return builder.ToString();
    }

    private static string BuildPatchCronJobSuspendCommand(string contextName, string namespaceName, string resourceName, bool suspend)
    {
        var builder = new StringBuilder("kubectl");
        builder.Append(" --context ").Append(EscapeArgument(contextName));
        builder.Append(" -n ").Append(EscapeArgument(namespaceName));
        builder.Append(" patch cronjob/").Append(EscapeArgument(resourceName));
        builder.Append(" --type merge -p ");
        builder.Append(EscapeArgument($"{{\"spec\":{{\"suspend\":{suspend.ToString().ToLowerInvariant()}}}}}"));
        return builder.ToString();
    }

    private static string BuildCordonNodeCommand(string contextName, string resourceName)
    {
        var builder = new StringBuilder("kubectl");
        builder.Append(" --context ").Append(EscapeArgument(contextName));
        builder.Append(" cordon ").Append(EscapeArgument(resourceName));
        return builder.ToString();
    }

    private static string BuildUncordonNodeCommand(string contextName, string resourceName)
    {
        var builder = new StringBuilder("kubectl");
        builder.Append(" --context ").Append(EscapeArgument(contextName));
        builder.Append(" uncordon ").Append(EscapeArgument(resourceName));
        return builder.ToString();
    }

    private static string BuildDrainNodeCommand(string contextName, string resourceName)
    {
        var builder = new StringBuilder("kubectl");
        builder.Append(" --context ").Append(EscapeArgument(contextName));
        builder.Append(" drain ").Append(EscapeArgument(resourceName));
        builder.Append(" --ignore-daemonsets --delete-emptydir-data");
        return builder.ToString();
    }

    private static bool IsNamespaced(KubeResourceKind kind)
    {
        return IsNamespaced(kind, customResourceType: null);
    }

    private static bool IsNamespaced(KubeResourceKind kind, KubeCustomResourceType? customResourceType)
    {
        return kind switch
        {
            KubeResourceKind.CustomResource => customResourceType?.Namespaced is not false,
            _ => kind is not KubeResourceKind.Namespace and not KubeResourceKind.Node
        };
    }

    private static string GetKubectlResourceName(KubeResourceKind kind, KubeCustomResourceType? customResourceType)
    {
        return kind switch
        {
            KubeResourceKind.CustomResource => customResourceType?.Plural
                ?? throw new ArgumentOutOfRangeException(nameof(customResourceType), customResourceType, "A custom resource type is required."),
            KubeResourceKind.Namespace => "namespaces",
            KubeResourceKind.Node => "nodes",
            KubeResourceKind.Pod => "pods",
            KubeResourceKind.Deployment => "deployments",
            KubeResourceKind.ReplicaSet => "replicasets",
            KubeResourceKind.StatefulSet => "statefulsets",
            KubeResourceKind.DaemonSet => "daemonsets",
            KubeResourceKind.Service => "services",
            KubeResourceKind.Ingress => "ingresses",
            KubeResourceKind.ConfigMap => "configmaps",
            KubeResourceKind.Secret => "secrets",
            KubeResourceKind.Job => "jobs",
            KubeResourceKind.CronJob => "cronjobs",
            KubeResourceKind.Event => "events",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported Kubernetes resource kind.")
        };
    }

    private static string GetDisplayKindName(KubeResourceKind kind, KubeCustomResourceType? customResourceType)
    {
        return kind switch
        {
            KubeResourceKind.CustomResource => customResourceType?.Kind ?? "CustomResource",
            _ => kind.ToString()
        };
    }

    private static string GetEventKindName(KubeResourceKind kind)
    {
        return kind switch
        {
            KubeResourceKind.CustomResource => "CustomResource",
            KubeResourceKind.Namespace => "Namespace",
            KubeResourceKind.Node => "Node",
            KubeResourceKind.Pod => "Pod",
            KubeResourceKind.Deployment => "Deployment",
            KubeResourceKind.ReplicaSet => "ReplicaSet",
            KubeResourceKind.StatefulSet => "StatefulSet",
            KubeResourceKind.DaemonSet => "DaemonSet",
            KubeResourceKind.Service => "Service",
            KubeResourceKind.Ingress => "Ingress",
            KubeResourceKind.ConfigMap => "ConfigMap",
            KubeResourceKind.Secret => "Secret",
            KubeResourceKind.Job => "Job",
            KubeResourceKind.CronJob => "CronJob",
            KubeResourceKind.Event => "Event",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported Kubernetes resource kind.")
        };
    }

    private static string EscapeArgument(string value)
    {
        return value.IndexOfAny([' ', '"']) >= 0
            ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : value;
    }
}
