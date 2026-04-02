using k8s;
using k8s.Autorest;
using k8s.Models;
using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Kube;

internal static class KubeDeploymentRollbackPlanner
{
    private const string RevisionAnnotation = "deployment.kubernetes.io/revision";
    private const string ChangeCauseAnnotation = "kubernetes.io/change-cause";

    public static async Task<(KubeDeploymentRollbackResolution Resolution, KubeActionPreviewPermissionCoverage Coverage)> ResolveAsync(
        Kubernetes client,
        V1Deployment deployment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(deployment);

        var namespaceName = deployment.Metadata?.NamespaceProperty?.Trim();
        if (string.IsNullOrWhiteSpace(namespaceName))
        {
            return (Resolve(deployment, []), KubeActionPreviewPermissionCoverage.Empty);
        }

        var labelSelector = CreateLabelSelector(deployment.Spec?.Selector?.MatchLabels);
        if (string.IsNullOrWhiteSpace(labelSelector))
        {
            return (Resolve(deployment, []), KubeActionPreviewPermissionCoverage.Empty);
        }

        try
        {
            var replicaSets = await client.ListNamespacedReplicaSetAsync(
                namespaceName,
                labelSelector: labelSelector,
                cancellationToken: cancellationToken);

            return (Resolve(deployment, replicaSets.Items.ToArray()), KubeActionPreviewPermissionCoverage.Empty);
        }
        catch (HttpOperationException exception) when (exception.Response.StatusCode is System.Net.HttpStatusCode.NotFound)
        {
            return (Resolve(deployment, []), KubeActionPreviewPermissionCoverage.Empty);
        }
        catch (HttpOperationException exception) when (exception.Response.StatusCode is System.Net.HttpStatusCode.Forbidden)
        {
            return (
                Resolve(deployment, []),
                KubeActionPreviewPermissionCoverage.Create(
                    new KubeActionPermissionBlocker(
                        Scope: $"Retained rollout history for Deployment/{deployment.Metadata?.Name ?? "unknown"} in namespace {namespaceName}",
                        Summary: "Kubernetes RBAC limited preview visibility for retained rollout history.",
                        Detail: $"The preview could not inspect retained ReplicaSet history in namespace '{namespaceName}', so rollback target discovery is RBAC-limited. {exception.Message}"),
                    new KeyValuePair<string, string>("Retained revisions", "RBAC-limited")));
        }
    }

    public static KubeDeploymentRollbackResolution Resolve(V1Deployment deployment, IReadOnlyList<V1ReplicaSet> replicaSets)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        ArgumentNullException.ThrowIfNull(replicaSets);

        var deploymentName = deployment.Metadata?.Name?.Trim();
        var ownedReplicaSets = replicaSets
            .Where(replicaSet => IsOwnedByDeployment(replicaSet, deploymentName))
            .ToArray();

        var revisionGroups = ownedReplicaSets
            .Select(replicaSet => new ReplicaSetRevision(replicaSet, TryGetRevision(replicaSet.Metadata?.Annotations)))
            .Where(static entry => entry.Revision.HasValue && entry.ReplicaSet.Spec?.Template is not null)
            .GroupBy(static entry => entry.Revision!.Value)
            .OrderByDescending(static group => group.Key)
            .ToArray();

        var currentRevision = TryGetRevision(deployment.Metadata?.Annotations);
        var usedReplicaSetRevisionFallback = false;

        IGrouping<int, ReplicaSetRevision>? currentRevisionGroup = null;
        if (currentRevision.HasValue)
        {
            currentRevisionGroup = revisionGroups.FirstOrDefault(group => group.Key == currentRevision.Value);
        }

        if (currentRevisionGroup is null && revisionGroups.Length > 0)
        {
            currentRevisionGroup = revisionGroups[0];
            currentRevision = currentRevisionGroup.Key;
            usedReplicaSetRevisionFallback = true;
        }

        var previousRevisionGroup = currentRevisionGroup is null
            ? revisionGroups.Skip(1).FirstOrDefault()
            : revisionGroups.FirstOrDefault(group => group.Key < currentRevisionGroup.Key);

        var currentReplicaSet = currentRevisionGroup is null
            ? null
            : PickBestReplicaSet(currentRevisionGroup);
        var previousReplicaSet = previousRevisionGroup is null
            ? null
            : PickBestReplicaSet(previousRevisionGroup);

        return new KubeDeploymentRollbackResolution(
            CurrentReplicaSet: currentReplicaSet,
            PreviousReplicaSet: previousReplicaSet,
            CurrentRevision: currentRevision,
            PreviousRevision: previousRevisionGroup?.Key,
            RetainedRevisionCount: revisionGroups.Length,
            UsedReplicaSetRevisionFallback: usedReplicaSetRevisionFallback,
            PreviousChangeCause: TryGetChangeCause(previousReplicaSet));
    }

    public static string GetTemplateImageSummary(V1PodTemplateSpec? template)
    {
        if (template?.Spec?.Containers is null || template.Spec.Containers.Count is 0)
        {
            return "No container images recorded";
        }

        return string.Join(
            ", ",
            template.Spec.Containers
                .Where(static container => !string.IsNullOrWhiteSpace(container.Image))
                .Select(static container => string.IsNullOrWhiteSpace(container.Name)
                    ? container.Image!
                    : $"{container.Name}={container.Image}")
                .DefaultIfEmpty("No container images recorded"));
    }

    private static bool IsOwnedByDeployment(V1ReplicaSet replicaSet, string? deploymentName)
    {
        if (string.IsNullOrWhiteSpace(deploymentName))
        {
            return false;
        }

        var owner = replicaSet.Metadata?.OwnerReferences?
            .FirstOrDefault(static reference =>
                reference.Controller == true &&
                string.Equals(reference.Kind, "Deployment", StringComparison.Ordinal));

        return owner is not null &&
               string.Equals(owner.Name, deploymentName, StringComparison.Ordinal);
    }

    private static V1ReplicaSet PickBestReplicaSet(IEnumerable<ReplicaSetRevision> entries)
    {
        return entries
            .OrderByDescending(static entry => entry.ReplicaSet.Metadata?.CreationTimestamp)
            .ThenByDescending(static entry => entry.ReplicaSet.Metadata?.Name, StringComparer.OrdinalIgnoreCase)
            .Select(static entry => entry.ReplicaSet)
            .First();
    }

    private static int? TryGetRevision(IDictionary<string, string>? annotations)
    {
        if (annotations is null ||
            !annotations.TryGetValue(RevisionAnnotation, out var rawValue) ||
            string.IsNullOrWhiteSpace(rawValue) ||
            !int.TryParse(rawValue.Trim(), out var parsedRevision))
        {
            return null;
        }

        return parsedRevision;
    }

    private static string? TryGetChangeCause(V1ReplicaSet? replicaSet)
    {
        return replicaSet?.Metadata?.Annotations is { } annotations &&
               annotations.TryGetValue(ChangeCauseAnnotation, out var rawValue) &&
               !string.IsNullOrWhiteSpace(rawValue)
            ? rawValue.Trim()
            : null;
    }

    private static string? CreateLabelSelector(IEnumerable<KeyValuePair<string, string>>? selector)
    {
        if (selector is null)
        {
            return null;
        }

        var parts = selector
            .Where(static pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            .Select(static pair => $"{pair.Key}={pair.Value}")
            .ToArray();

        return parts.Length is 0
            ? null
            : string.Join(",", parts);
    }

    private sealed record ReplicaSetRevision(V1ReplicaSet ReplicaSet, int? Revision);
}

internal sealed record KubeDeploymentRollbackResolution(
    V1ReplicaSet? CurrentReplicaSet,
    V1ReplicaSet? PreviousReplicaSet,
    int? CurrentRevision,
    int? PreviousRevision,
    int RetainedRevisionCount,
    bool UsedReplicaSetRevisionFallback,
    string? PreviousChangeCause)
{
    public bool CanRollback => PreviousReplicaSet?.Spec?.Template is not null && PreviousRevision.HasValue;
}
