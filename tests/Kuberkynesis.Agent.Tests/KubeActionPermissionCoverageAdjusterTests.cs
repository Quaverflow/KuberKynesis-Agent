using Kuberkynesis.Agent.Kube;
using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Tests;

public sealed class KubeActionPermissionCoverageAdjusterTests
{
    [Fact]
    public void Apply_WithRestrictedPreviewScope_AddsBlockersWarningsAndFactOverrides()
    {
        var preview = CreatePreview(
            confidence: KubeActionPreviewConfidence.High,
            coverageSummary: "Current-state coverage is strong for the current target and controller evidence.",
            facts:
            [
                new KubeActionPreviewFact("Matching pods", "2"),
                new KubeActionPreviewFact("Target replicas", "3")
            ]);

        var adjusted = KubeActionPermissionCoverageAdjuster.Apply(
            preview,
            KubeActionPreviewPermissionCoverage.Create(
                new KubeActionPermissionBlocker(
                    Scope: "Matching pods in namespace checkout-prod",
                    Summary: "Kubernetes RBAC limited preview visibility for the current workload scope.",
                    Detail: "The preview could not inspect matching pods in namespace 'checkout-prod', so pod counts and rollout membership are RBAC-limited."),
                new KeyValuePair<string, string>("Matching pods", "RBAC-limited")));

        Assert.Equal(KubeActionPreviewConfidence.Medium, adjusted.Confidence);
        Assert.Equal("Current-state coverage is partial because some affected scope could not be inspected under current RBAC.", adjusted.CoverageSummary);
        Assert.Contains(adjusted.Warnings, warning => warning.Contains("partial because Kubernetes RBAC limited visibility", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(adjusted.CoverageLimits, limit => limit.Contains("rollout membership are RBAC-limited", StringComparison.Ordinal));
        Assert.Contains(adjusted.Guardrails.Reasons, reason => reason.Contains("partially hidden by Kubernetes RBAC", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("RBAC-limited", adjusted.Facts.Single(fact => fact.Label == "Matching pods").Value);
        Assert.Single(adjusted.PermissionBlockers);
    }

    [Fact]
    public void Apply_WithExistingPartialSummary_KeepsTheOriginalSummary()
    {
        var preview = CreatePreview(
            confidence: KubeActionPreviewConfidence.Medium,
            coverageSummary: "Drain impact can be previewed, but direct execution stays intentionally unavailable in this slice. Coverage is partial from current cluster evidence.");

        var adjusted = KubeActionPermissionCoverageAdjuster.Apply(
            preview,
            KubeActionPreviewPermissionCoverage.Create(
                new KubeActionPermissionBlocker(
                    Scope: "Pods scheduled on node kind-worker",
                    Summary: "Kubernetes RBAC limited preview visibility for the node workload scope.",
                    Detail: "The preview could not inspect pods scheduled on node 'kind-worker', so workload counts and namespace breadth are RBAC-limited."),
                new KeyValuePair<string, string>("Scheduled pods", "RBAC-limited")));

        Assert.Equal(preview.CoverageSummary, adjusted.CoverageSummary);
        Assert.Equal(KubeActionPreviewConfidence.Medium, adjusted.Confidence);
        Assert.Equal("RBAC-limited", adjusted.Facts.Single(fact => fact.Label == "Scheduled pods").Value);
    }

    private static KubeActionPreviewResponse CreatePreview(
        KubeActionPreviewConfidence confidence,
        string coverageSummary,
        IReadOnlyList<KubeActionPreviewFact>? facts = null)
    {
        return new KubeActionPreviewResponse(
            Action: KubeActionKind.ScaleDeployment,
            Resource: new KubeResourceIdentity("kind-kuberkynesis-lab", KubeResourceKind.Deployment, "checkout-prod", "checkout-api"),
            Summary: "Deployment/checkout-api would scale up by 1: 2 -> 3 replicas.",
            Confidence: confidence,
            Guardrails: new KubeActionGuardrailDecision(
                RiskLevel: KubeActionRiskLevel.Medium,
                ConfirmationLevel: KubeActionConfirmationLevel.TypedConfirmation,
                IsExecutionBlocked: false,
                Summary: "Execution should require typed confirmation.",
                AcknowledgementHint: "Type deployment/checkout-api to confirm later.",
                Reasons:
                [
                    "The target scope is production, so policy raises this above low-risk execution."
                ]),
            CoverageSummary: coverageSummary,
            Facts: facts ?? [new KubeActionPreviewFact("Scheduled pods", "8")],
            Warnings: [],
            Notes: [],
            SaferAlternatives: [],
            AffectedResources: [],
            TransparencyCommands: []);
    }
}
