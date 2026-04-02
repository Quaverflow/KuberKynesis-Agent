using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Kube;

internal static class KubeActionImpactReportFactory
{
    public static KubeActionImpactReport Build(KubeActionPreviewResponse preview)
    {
        ArgumentNullException.ThrowIfNull(preview);

        var directTargets = new List<KubeRelatedResource>();
        var indirectImpacts = new List<KubeRelatedResource>();
        var sharedDependencies = new List<KubeRelatedResource>();

        var primaryTarget = BuildPrimaryTarget(preview.Resource);
        if (primaryTarget is not null)
        {
            directTargets.Add(primaryTarget);
        }

        foreach (var affectedResource in preview.AffectedResources)
        {
            switch (Classify(affectedResource))
            {
                case KubeActionImpactRelationship.SharedDependency:
                    sharedDependencies.Add(affectedResource);
                    break;
                case KubeActionImpactRelationship.DirectTarget:
                    directTargets.Add(affectedResource);
                    break;
                default:
                    indirectImpacts.Add(affectedResource);
                    break;
            }
        }

        return new KubeActionImpactReport(
            ActionSummary: preview.Summary,
            DirectTargets: directTargets.Distinct().ToArray(),
            IndirectImpacts: indirectImpacts.Distinct().ToArray(),
            SharedDependencies: sharedDependencies.Distinct().ToArray(),
            PermissionBlockers: preview.PermissionBlockers.Distinct().ToArray(),
            EquivalentOperations: preview.TransparencyCommands.Distinct().ToArray());
    }

    private static KubeRelatedResource? BuildPrimaryTarget(KubeResourceIdentity resource)
    {
        if (string.IsNullOrWhiteSpace(resource.Name))
        {
            return null;
        }

        return new KubeRelatedResource(
            Relationship: "Direct target",
            Kind: resource.Kind,
            ApiVersion: "unknown",
            Name: resource.Name,
            Namespace: resource.Namespace,
            Status: null,
            Summary: null);
    }

    private static KubeActionImpactRelationship Classify(KubeRelatedResource resource)
    {
        if (resource.Kind is KubeResourceKind.ConfigMap or KubeResourceKind.Secret ||
            resource.Relationship.StartsWith("Uses ", StringComparison.OrdinalIgnoreCase))
        {
            return KubeActionImpactRelationship.SharedDependency;
        }

        if (resource.Relationship.StartsWith("Current pod", StringComparison.OrdinalIgnoreCase) ||
            resource.Relationship.StartsWith("Active job", StringComparison.OrdinalIgnoreCase) ||
            resource.Relationship.StartsWith("Scheduled pod", StringComparison.OrdinalIgnoreCase))
        {
            return KubeActionImpactRelationship.DirectTarget;
        }

        return KubeActionImpactRelationship.IndirectImpact;
    }

    private enum KubeActionImpactRelationship
    {
        DirectTarget,
        IndirectImpact,
        SharedDependency
    }
}
