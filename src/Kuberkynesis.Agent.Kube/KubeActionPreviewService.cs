using k8s;
using k8s.Autorest;
using k8s.Models;
using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Kube;

public sealed class KubeActionPreviewService(
    IKubeConfigLoader kubeConfigLoader,
    KubeActionGuardrailEngine guardrailEngine,
    KubeActionImpactEngine impactEngine)
{
    public async Task<KubeActionPreviewResponse> GetPreviewAsync(KubeActionPreviewRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.ContextName))
        {
            throw new ArgumentException("A kube context name is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ArgumentException("A resource name is required.", nameof(request));
        }

        var loadResult = kubeConfigLoader.Load();

        if (loadResult.Contexts.Count is 0)
        {
            throw new ArgumentException("No kube contexts were found.");
        }

        var context = KubeResourceQueryService.ResolveTargetContexts([request.ContextName], loadResult).Single();

        if (context.Status is KubeContextStatus.ConfigurationError)
        {
            throw new ArgumentException(context.StatusMessage ?? $"The kube context '{context.Name}' is invalid.");
        }

        using var client = kubeConfigLoader.CreateClient(loadResult, context.Name);

        var preview = request.Action switch
        {
            KubeActionKind.ScaleDeployment => await GetScaleDeploymentPreviewAsync(client, context.Name, request, cancellationToken),
            KubeActionKind.RestartDeploymentRollout => await GetRestartDeploymentPreviewAsync(client, context.Name, request, cancellationToken),
            KubeActionKind.RollbackDeploymentRollout => await GetRollbackDeploymentPreviewAsync(client, context.Name, request, cancellationToken),
            KubeActionKind.DeletePod => await GetDeletePodPreviewAsync(client, context.Name, request, cancellationToken),
            KubeActionKind.ScaleStatefulSet => await GetScaleStatefulSetPreviewAsync(client, context.Name, request, cancellationToken),
            KubeActionKind.RestartDaemonSetRollout => await GetRestartDaemonSetPreviewAsync(client, context.Name, request, cancellationToken),
            KubeActionKind.DeleteJob => await GetDeleteJobPreviewAsync(client, context.Name, request, cancellationToken),
            KubeActionKind.SuspendCronJob => await GetCronJobSuspendPreviewAsync(client, context.Name, request, suspend: true, cancellationToken),
            KubeActionKind.ResumeCronJob => await GetCronJobSuspendPreviewAsync(client, context.Name, request, suspend: false, cancellationToken),
            KubeActionKind.CordonNode => await GetNodeSchedulingPreviewAsync(client, context.Name, request, cordon: true, cancellationToken),
            KubeActionKind.UncordonNode => await GetNodeSchedulingPreviewAsync(client, context.Name, request, cordon: false, cancellationToken),
            KubeActionKind.DrainNode => await GetDrainNodePreviewAsync(client, context.Name, request, cancellationToken),
            _ => throw new ArgumentException($"The action '{request.Action}' is not supported yet.", nameof(request))
        };

        var guardrailAdjustedPreview = await guardrailEngine.FinalizeAsync(
            client,
            request,
            preview,
            cancellationToken);

        return impactEngine.Attach(guardrailAdjustedPreview);
    }

    private static async Task<KubeActionPreviewResponse> GetScaleDeploymentPreviewAsync(
        Kubernetes client,
        string contextName,
        KubeActionPreviewRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Kind is not KubeResourceKind.Deployment)
        {
            throw new ArgumentException("Scale preview is currently only supported for Deployment resources.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Namespace))
        {
            throw new ArgumentException("A namespace is required for deployment scale preview.", nameof(request));
        }

        if (request.TargetReplicas is null or < 0 or > 500)
        {
            throw new ArgumentException("Scale preview requires a target replica count between 0 and 500.", nameof(request));
        }

        var deployment = await client.ReadNamespacedDeploymentAsync(
            request.Name.Trim(),
            request.Namespace.Trim(),
            cancellationToken: cancellationToken);

        var (matchingPods, podCoverage) = await ListPodsBySelectorAsync(
            client,
            request.Namespace.Trim(),
            deployment.Spec?.Selector?.MatchLabels,
            cancellationToken);

        return KubeActionPermissionCoverageAdjuster.Apply(
            KubeActionPreviewFactory.CreateScaleDeploymentPreview(
            contextName,
            deployment,
            matchingPods,
            request.TargetReplicas.Value,
            request.LocalEnvironmentRules),
            podCoverage);
    }

    private static async Task<KubeActionPreviewResponse> GetRestartDeploymentPreviewAsync(
        Kubernetes client,
        string contextName,
        KubeActionPreviewRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Kind is not KubeResourceKind.Deployment)
        {
            throw new ArgumentException("Rollout restart preview is currently only supported for Deployment resources.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Namespace))
        {
            throw new ArgumentException("A namespace is required for deployment rollout restart preview.", nameof(request));
        }

        var deployment = await client.ReadNamespacedDeploymentAsync(
            request.Name.Trim(),
            request.Namespace.Trim(),
            cancellationToken: cancellationToken);

        var (matchingPods, podCoverage) = await ListPodsBySelectorAsync(
            client,
            request.Namespace.Trim(),
            deployment.Spec?.Selector?.MatchLabels,
            cancellationToken);

        var (budgets, pdbCoverage) = await ListNamespacedPodDisruptionBudgetsAsync(
            client,
            request.Namespace.Trim(),
            cancellationToken,
            matchedCountFactLabel: null);

        var disruptionBudgetImpact = KubePodDisruptionBudgetMatcher.BuildImpact(
            budgets,
            matchingPods);

        return KubeActionPermissionCoverageAdjuster.Apply(
            KubeActionPreviewFactory.CreateRestartDeploymentPreview(
            contextName,
            deployment,
            matchingPods,
            disruptionBudgetImpact,
            request.LocalEnvironmentRules),
            KubeActionPreviewPermissionCoverage.Combine(podCoverage, pdbCoverage));
    }

    private static async Task<KubeActionPreviewResponse> GetRollbackDeploymentPreviewAsync(
        Kubernetes client,
        string contextName,
        KubeActionPreviewRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Kind is not KubeResourceKind.Deployment)
        {
            throw new ArgumentException("Rollout undo preview is currently only supported for Deployment resources.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Namespace))
        {
            throw new ArgumentException("A namespace is required for deployment rollout undo preview.", nameof(request));
        }

        var namespaceName = request.Namespace.Trim();
        var deployment = await client.ReadNamespacedDeploymentAsync(
            request.Name.Trim(),
            namespaceName,
            cancellationToken: cancellationToken);

        var (matchingPods, podCoverage) = await ListPodsBySelectorAsync(
            client,
            namespaceName,
            deployment.Spec?.Selector?.MatchLabels,
            cancellationToken);

        var (rollbackResolution, rollbackCoverage) = await KubeDeploymentRollbackPlanner.ResolveAsync(
            client,
            deployment,
            cancellationToken);

        var (budgets, pdbCoverage) = await ListNamespacedPodDisruptionBudgetsAsync(
            client,
            namespaceName,
            cancellationToken,
            matchedCountFactLabel: null);

        var disruptionBudgetImpact = KubePodDisruptionBudgetMatcher.BuildImpact(
            budgets,
            matchingPods);

        return KubeActionPermissionCoverageAdjuster.Apply(
            KubeActionPreviewFactory.CreateRollbackDeploymentPreview(
                contextName,
                deployment,
                matchingPods,
                rollbackResolution,
                disruptionBudgetImpact,
                rollbackCoverage.HasRestrictions,
                request.LocalEnvironmentRules),
            KubeActionPreviewPermissionCoverage.Combine(podCoverage, rollbackCoverage, pdbCoverage));
    }

    private static async Task<KubeActionPreviewResponse> GetDeletePodPreviewAsync(
        Kubernetes client,
        string contextName,
        KubeActionPreviewRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Kind is not KubeResourceKind.Pod)
        {
            throw new ArgumentException("Delete pod preview is currently only supported for Pod resources.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Namespace))
        {
            throw new ArgumentException("A namespace is required for pod delete preview.", nameof(request));
        }

        var namespaceName = request.Namespace.Trim();
        var pod = await client.ReadNamespacedPodAsync(
            request.Name.Trim(),
            namespaceName,
            cancellationToken: cancellationToken);

        var immediateOwnerReference = SelectControllerOwnerReference(pod.Metadata?.OwnerReferences);
        var immediateOwner = CreateOwnerResource(immediateOwnerReference, namespaceName);
        KubeRelatedResource? rolloutOwner = null;
        var replacementLikely = false;
        var rolloutOwnerCoverage = KubeActionPreviewPermissionCoverage.Empty;

        if (immediateOwnerReference is not null)
        {
            switch (MapOwnerKind(immediateOwnerReference.Kind))
            {
                case KubeResourceKind.ReplicaSet:
                {
                    replacementLikely = true;
                    var resolvedRolloutOwner = await TryResolveReplicaSetRolloutOwnerAsync(
                        client,
                        immediateOwnerReference.Name,
                        namespaceName,
                        cancellationToken);

                    rolloutOwner = resolvedRolloutOwner.RolloutOwner;
                    rolloutOwnerCoverage = resolvedRolloutOwner.Coverage;

                    break;
                }
                case KubeResourceKind.StatefulSet:
                case KubeResourceKind.DaemonSet:
                    replacementLikely = true;
                    break;
                default:
                    replacementLikely = false;
                    break;
            }
        }

        var (podDisruptionBudgets, pdbCoverageForPodDelete) = await ListNamespacedPodDisruptionBudgetsAsync(
            client,
            namespaceName,
            cancellationToken,
            matchedCountFactLabel: null);

        var disruptionBudgetImpact = KubePodDisruptionBudgetMatcher.BuildImpact(
            podDisruptionBudgets,
            [pod]);

        return KubeActionPermissionCoverageAdjuster.Apply(
            KubeActionPreviewFactory.CreateDeletePodPreview(
            contextName,
            pod,
            immediateOwner,
            rolloutOwner,
            replacementLikely,
            disruptionBudgetImpact,
            request.LocalEnvironmentRules),
            KubeActionPreviewPermissionCoverage.Combine(rolloutOwnerCoverage, pdbCoverageForPodDelete));
    }

    private static async Task<KubeActionPreviewResponse> GetScaleStatefulSetPreviewAsync(
        Kubernetes client,
        string contextName,
        KubeActionPreviewRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Kind is not KubeResourceKind.StatefulSet)
        {
            throw new ArgumentException("Scale preview is currently only supported for StatefulSet resources.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Namespace))
        {
            throw new ArgumentException("A namespace is required for statefulset scale preview.", nameof(request));
        }

        if (request.TargetReplicas is null or < 0 or > 500)
        {
            throw new ArgumentException("StatefulSet scale preview requires a target replica count between 0 and 500.", nameof(request));
        }

        var statefulSet = await client.ReadNamespacedStatefulSetAsync(
            request.Name.Trim(),
            request.Namespace.Trim(),
            cancellationToken: cancellationToken);

        var (matchingPods, podCoverage) = await ListPodsBySelectorAsync(
            client,
            request.Namespace.Trim(),
            statefulSet.Spec?.Selector?.MatchLabels,
            cancellationToken);

        return KubeActionPermissionCoverageAdjuster.Apply(
            KubeActionPreviewFactory.CreateScaleStatefulSetPreview(
            contextName,
            statefulSet,
            matchingPods,
            request.TargetReplicas.Value,
            request.LocalEnvironmentRules),
            podCoverage);
    }

    private static async Task<KubeActionPreviewResponse> GetRestartDaemonSetPreviewAsync(
        Kubernetes client,
        string contextName,
        KubeActionPreviewRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Kind is not KubeResourceKind.DaemonSet)
        {
            throw new ArgumentException("Rollout restart preview is currently only supported for DaemonSet resources.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Namespace))
        {
            throw new ArgumentException("A namespace is required for daemonset rollout restart preview.", nameof(request));
        }

        var daemonSet = await client.ReadNamespacedDaemonSetAsync(
            request.Name.Trim(),
            request.Namespace.Trim(),
            cancellationToken: cancellationToken);

        var (matchingPods, podCoverage) = await ListPodsBySelectorAsync(
            client,
            request.Namespace.Trim(),
            daemonSet.Spec?.Selector?.MatchLabels,
            cancellationToken);

        var (budgets, pdbCoverage) = await ListNamespacedPodDisruptionBudgetsAsync(
            client,
            request.Namespace.Trim(),
            cancellationToken,
            matchedCountFactLabel: null);

        var disruptionBudgetImpact = KubePodDisruptionBudgetMatcher.BuildImpact(
            budgets,
            matchingPods);

        return KubeActionPermissionCoverageAdjuster.Apply(
            KubeActionPreviewFactory.CreateRestartDaemonSetPreview(
            contextName,
            daemonSet,
            matchingPods,
            disruptionBudgetImpact,
            request.LocalEnvironmentRules),
            KubeActionPreviewPermissionCoverage.Combine(podCoverage, pdbCoverage));
    }

    private static async Task<KubeActionPreviewResponse> GetDeleteJobPreviewAsync(
        Kubernetes client,
        string contextName,
        KubeActionPreviewRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Kind is not KubeResourceKind.Job)
        {
            throw new ArgumentException("Delete job preview is currently only supported for Job resources.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Namespace))
        {
            throw new ArgumentException("A namespace is required for job delete preview.", nameof(request));
        }

        var job = await client.ReadNamespacedJobAsync(
            request.Name.Trim(),
            request.Namespace.Trim(),
            cancellationToken: cancellationToken);

        return KubeActionPreviewFactory.CreateDeleteJobPreview(contextName, job, request.LocalEnvironmentRules);
    }

    private static async Task<KubeActionPreviewResponse> GetCronJobSuspendPreviewAsync(
        Kubernetes client,
        string contextName,
        KubeActionPreviewRequest request,
        bool suspend,
        CancellationToken cancellationToken)
    {
        if (request.Kind is not KubeResourceKind.CronJob)
        {
            throw new ArgumentException("CronJob preview is currently only supported for CronJob resources.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Namespace))
        {
            throw new ArgumentException("A namespace is required for CronJob preview.", nameof(request));
        }

        var cronJob = await client.ReadNamespacedCronJobAsync(
            request.Name.Trim(),
            request.Namespace.Trim(),
            cancellationToken: cancellationToken);

        return KubeActionPreviewFactory.CreateCronJobSuspendPreview(
            contextName,
            cronJob,
            suspend,
            request.LocalEnvironmentRules);
    }

    private static async Task<KubeActionPreviewResponse> GetNodeSchedulingPreviewAsync(
        Kubernetes client,
        string contextName,
        KubeActionPreviewRequest request,
        bool cordon,
        CancellationToken cancellationToken)
    {
        if (request.Kind is not KubeResourceKind.Node)
        {
            throw new ArgumentException("Node scheduling preview is currently only supported for Node resources.", nameof(request));
        }

        var node = await client.ReadNodeAsync(
            request.Name.Trim(),
            cancellationToken: cancellationToken);

        var (scheduledPods, podCoverage) = await ListPodsOnNodeAsync(client, request.Name.Trim(), cancellationToken);

        return KubeActionPermissionCoverageAdjuster.Apply(
            KubeActionPreviewFactory.CreateNodeSchedulingPreview(
            contextName,
            node,
            scheduledPods,
            cordon,
            request.LocalEnvironmentRules),
            podCoverage);
    }

    private static async Task<KubeActionPreviewResponse> GetDrainNodePreviewAsync(
        Kubernetes client,
        string contextName,
        KubeActionPreviewRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Kind is not KubeResourceKind.Node)
        {
            throw new ArgumentException("Drain preview is currently only supported for Node resources.", nameof(request));
        }

        var node = await client.ReadNodeAsync(
            request.Name.Trim(),
            cancellationToken: cancellationToken);

        var (scheduledPods, podCoverage) = await ListPodsOnNodeAsync(client, request.Name.Trim(), cancellationToken);
        var (budgets, pdbCoverage) = await ListPodDisruptionBudgetsForPodsAsync(client, scheduledPods, cancellationToken);
        var disruptionBudgetImpact = KubePodDisruptionBudgetMatcher.BuildImpact(
            budgets,
            scheduledPods);

        return KubeActionPermissionCoverageAdjuster.Apply(
            KubeActionPreviewFactory.CreateDrainNodePreview(
            contextName,
            node,
            scheduledPods,
            disruptionBudgetImpact,
            request.LocalEnvironmentRules),
            KubeActionPreviewPermissionCoverage.Combine(podCoverage, pdbCoverage));
    }

    private static async Task<(IReadOnlyList<V1Pod> Pods, KubeActionPreviewPermissionCoverage Coverage)> ListPodsBySelectorAsync(
        Kubernetes client,
        string namespaceName,
        IEnumerable<KeyValuePair<string, string>>? selector,
        CancellationToken cancellationToken)
    {
        var labelSelector = CreateLabelSelector(selector);

        if (string.IsNullOrWhiteSpace(labelSelector))
        {
            return ([], KubeActionPreviewPermissionCoverage.Empty);
        }

        try
        {
            var pods = await client.ListNamespacedPodAsync(
                namespaceName,
                labelSelector: labelSelector,
                cancellationToken: cancellationToken);

            return (pods.Items.ToArray(), KubeActionPreviewPermissionCoverage.Empty);
        }
        catch (HttpOperationException exception) when (exception.Response.StatusCode is System.Net.HttpStatusCode.NotFound)
        {
            return ([], KubeActionPreviewPermissionCoverage.Empty);
        }
        catch (HttpOperationException exception) when (exception.Response.StatusCode is System.Net.HttpStatusCode.Forbidden)
        {
            return (
                [],
                KubeActionPreviewPermissionCoverage.Create(
                    new KubeActionPermissionBlocker(
                        Scope: $"Matching pods in namespace {namespaceName}",
                        Summary: "Kubernetes RBAC limited preview visibility for the current workload scope.",
                        Detail: $"The preview could not inspect matching pods in namespace '{namespaceName}', so pod counts and rollout membership are RBAC-limited. {exception.Message}"),
                    new KeyValuePair<string, string>("Matching pods", "RBAC-limited")));
        }
    }

    private static string? CreateLabelSelector(IEnumerable<KeyValuePair<string, string>>? selector)
    {
        if (selector is null)
        {
            return null;
        }

        var parts = selector
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => $"{pair.Key}={pair.Value}")
            .ToArray();

        return parts.Length is 0 ? null : string.Join(",", parts);
    }

    private static V1OwnerReference? SelectControllerOwnerReference(IEnumerable<V1OwnerReference>? ownerReferences)
    {
        return ownerReferences?
            .FirstOrDefault(static owner => owner.Controller == true)
            ?? ownerReferences?.FirstOrDefault();
    }

    private static KubeRelatedResource? CreateOwnerResource(V1OwnerReference? ownerReference, string namespaceName)
    {
        if (ownerReference is null || string.IsNullOrWhiteSpace(ownerReference.Name))
        {
            return null;
        }

        var kind = MapOwnerKind(ownerReference.Kind);
        if (kind is null)
        {
            return null;
        }

        return new KubeRelatedResource(
            Relationship: "Owned by",
            Kind: kind.Value,
            ApiVersion: string.IsNullOrWhiteSpace(ownerReference.ApiVersion) ? "unknown" : ownerReference.ApiVersion,
            Name: ownerReference.Name,
            Namespace: namespaceName,
            Status: null,
            Summary: null);
    }

    private static async Task<(IReadOnlyList<V1Pod> Pods, KubeActionPreviewPermissionCoverage Coverage)> ListPodsOnNodeAsync(
        Kubernetes client,
        string nodeName,
        CancellationToken cancellationToken)
    {
        try
        {
            var pods = await client.ListPodForAllNamespacesAsync(
                fieldSelector: $"spec.nodeName={nodeName}",
                cancellationToken: cancellationToken);

            return (pods.Items.ToArray(), KubeActionPreviewPermissionCoverage.Empty);
        }
        catch (HttpOperationException exception) when (exception.Response.StatusCode is System.Net.HttpStatusCode.NotFound)
        {
            return ([], KubeActionPreviewPermissionCoverage.Empty);
        }
        catch (HttpOperationException exception) when (exception.Response.StatusCode is System.Net.HttpStatusCode.Forbidden)
        {
            return (
                [],
                KubeActionPreviewPermissionCoverage.Create(
                    new KubeActionPermissionBlocker(
                        Scope: $"Pods scheduled on node {nodeName}",
                        Summary: "Kubernetes RBAC limited preview visibility for the node workload scope.",
                        Detail: $"The preview could not inspect pods scheduled on node '{nodeName}', so workload counts and namespace breadth are RBAC-limited. {exception.Message}"),
                    new KeyValuePair<string, string>("Scheduled pods", "RBAC-limited")));
        }
    }

    private static async Task<(IReadOnlyList<V1PodDisruptionBudget> Budgets, KubeActionPreviewPermissionCoverage Coverage)> ListNamespacedPodDisruptionBudgetsAsync(
        Kubernetes client,
        string namespaceName,
        CancellationToken cancellationToken,
        string? matchedCountFactLabel)
    {
        try
        {
            var budgets = await client.ListNamespacedPodDisruptionBudgetAsync(
                namespaceName,
                cancellationToken: cancellationToken);

            return (budgets.Items.ToArray(), KubeActionPreviewPermissionCoverage.Empty);
        }
        catch (HttpOperationException exception) when (exception.Response.StatusCode is System.Net.HttpStatusCode.NotFound)
        {
            return ([], KubeActionPreviewPermissionCoverage.Empty);
        }
        catch (HttpOperationException exception) when (exception.Response.StatusCode is System.Net.HttpStatusCode.Forbidden)
        {
            var factOverrides = string.IsNullOrWhiteSpace(matchedCountFactLabel)
                ? Array.Empty<KeyValuePair<string, string>>()
                : [new KeyValuePair<string, string>(matchedCountFactLabel, "RBAC-limited")];

            return (
                [],
                KubeActionPreviewPermissionCoverage.Create(
                    new KubeActionPermissionBlocker(
                        Scope: $"PodDisruptionBudgets in namespace {namespaceName}",
                        Summary: "Kubernetes RBAC limited disruption-budget visibility for the current preview.",
                        Detail: $"The preview could not inspect PodDisruptionBudget objects in namespace '{namespaceName}', so disruption safety analysis is RBAC-limited. {exception.Message}"),
                    factOverrides));
        }
    }

    private static async Task<(IReadOnlyList<V1PodDisruptionBudget> Budgets, KubeActionPreviewPermissionCoverage Coverage)> ListPodDisruptionBudgetsForPodsAsync(
        Kubernetes client,
        IReadOnlyList<V1Pod> pods,
        CancellationToken cancellationToken)
    {
        var namespaces = pods
            .Select(static pod => pod.Metadata?.NamespaceProperty)
            .Where(static namespaceName => !string.IsNullOrWhiteSpace(namespaceName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (namespaces.Length is 0)
        {
            return ([], KubeActionPreviewPermissionCoverage.Empty);
        }

        var budgets = new List<V1PodDisruptionBudget>();
        var coverage = KubeActionPreviewPermissionCoverage.Empty;
        foreach (var namespaceName in namespaces)
        {
            var (namespaceBudgets, namespaceCoverage) = await ListNamespacedPodDisruptionBudgetsAsync(
                client,
                namespaceName!,
                cancellationToken,
                matchedCountFactLabel: "Matched PDBs");
            budgets.AddRange(namespaceBudgets);
            coverage = KubeActionPreviewPermissionCoverage.Combine(coverage, namespaceCoverage);
        }

        return (budgets, coverage);
    }

    private static async Task<(KubeRelatedResource? RolloutOwner, KubeActionPreviewPermissionCoverage Coverage)> TryResolveReplicaSetRolloutOwnerAsync(
        Kubernetes client,
        string replicaSetName,
        string namespaceName,
        CancellationToken cancellationToken)
    {
        try
        {
            var replicaSet = await client.ReadNamespacedReplicaSetAsync(
                replicaSetName,
                namespaceName,
                cancellationToken: cancellationToken);

            var replicaSetOwner = SelectControllerOwnerReference(replicaSet.Metadata?.OwnerReferences);
            return (CreateOwnerResource(replicaSetOwner, namespaceName), KubeActionPreviewPermissionCoverage.Empty);
        }
        catch (HttpOperationException exception) when (exception.Response.StatusCode is System.Net.HttpStatusCode.NotFound)
        {
            return (null, KubeActionPreviewPermissionCoverage.Empty);
        }
        catch (HttpOperationException exception) when (exception.Response.StatusCode is System.Net.HttpStatusCode.Forbidden)
        {
            return (
                null,
                KubeActionPreviewPermissionCoverage.Create(
                    new KubeActionPermissionBlocker(
                        Scope: $"Rollout owner inspection for ReplicaSet/{replicaSetName} in namespace {namespaceName}",
                        Summary: "Kubernetes RBAC limited preview visibility for the owning rollout.",
                        Detail: $"The preview could not inspect ReplicaSet/{replicaSetName} in namespace '{namespaceName}', so higher-level rollout ownership is RBAC-limited. {exception.Message}")));
        }
    }

    private static KubeResourceKind? MapOwnerKind(string? ownerKind)
    {
        return ownerKind switch
        {
            "ReplicaSet" => KubeResourceKind.ReplicaSet,
            "Deployment" => KubeResourceKind.Deployment,
            "StatefulSet" => KubeResourceKind.StatefulSet,
            "DaemonSet" => KubeResourceKind.DaemonSet,
            "Job" => KubeResourceKind.Job,
            "CronJob" => KubeResourceKind.CronJob,
            _ => null
        };
    }

}
