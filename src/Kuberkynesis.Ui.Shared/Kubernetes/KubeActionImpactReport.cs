namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubeActionImpactReport(
    string ActionSummary,
    IReadOnlyList<KubeRelatedResource> DirectTargets,
    IReadOnlyList<KubeRelatedResource> IndirectImpacts,
    IReadOnlyList<KubeRelatedResource> SharedDependencies,
    IReadOnlyList<KubeActionPermissionBlocker> PermissionBlockers,
    IReadOnlyList<KubectlCommandPreview> EquivalentOperations);
