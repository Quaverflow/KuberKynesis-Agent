namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubeActionPreviewResponse(
    KubeActionKind Action,
    KubeResourceIdentity Resource,
    string Summary,
    KubeActionPreviewConfidence Confidence,
    KubeActionGuardrailDecision Guardrails,
    string CoverageSummary,
    IReadOnlyList<KubeActionPreviewFact> Facts,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Notes,
    IReadOnlyList<KubeActionPreviewAlternative> SaferAlternatives,
    IReadOnlyList<KubeRelatedResource> AffectedResources,
    IReadOnlyList<KubectlCommandPreview> TransparencyCommands)
{
    public KubeActionAvailability Availability { get; init; } = KubeActionAvailability.PreviewAndExecute;

    public KubeActionEnvironmentKind Environment { get; init; } = KubeActionEnvironmentKind.Unknown;

    public IReadOnlyList<string> CoverageLimits { get; init; } = [];

    public KubeActionImpactReport? ImpactReport { get; init; }

    public KubeActionExecutionAccess ExecutionAccess { get; init; } = new(
        State: KubeActionExecutionAccessState.Unknown,
        Summary: "Kubernetes RBAC preflight was not available for this preview.",
        Detail: null);

    public IReadOnlyList<KubeActionPermissionBlocker> PermissionBlockers { get; init; } = [];
}
