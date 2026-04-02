using Kuberkynesis.Agent.Kube;
using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Tests;

public sealed class KubeActionGuardrailProfileAdjusterTests
{
    [Fact]
    public void ConservativeProfile_ElevatesConfirmationWithoutBlocking()
    {
        var preview = CreatePreview(
            confirmationLevel: KubeActionConfirmationLevel.ExplicitReview,
            blocked: false,
            riskLevel: KubeActionRiskLevel.Medium,
            confidence: KubeActionPreviewConfidence.Medium);

        var adjusted = KubeActionGuardrailProfileAdjuster.Apply(preview, KubeActionGuardrailProfile.Conservative);

        Assert.Equal(KubeActionConfirmationLevel.TypedConfirmation, adjusted.Guardrails.ConfirmationLevel);
        Assert.False(adjusted.Guardrails.IsExecutionBlocked);
        Assert.Contains("Conservative", adjusted.Guardrails.Reasons.Last(), StringComparison.Ordinal);
        Assert.Equal("Type deployment/orders-api to confirm later.", adjusted.Guardrails.AcknowledgementHint);
    }

    [Fact]
    public void StrictProfile_BlocksHighRiskPreviews()
    {
        var preview = CreatePreview(
            confirmationLevel: KubeActionConfirmationLevel.ExplicitReview,
            blocked: false,
            riskLevel: KubeActionRiskLevel.High,
            confidence: KubeActionPreviewConfidence.Medium);

        var adjusted = KubeActionGuardrailProfileAdjuster.Apply(preview, KubeActionGuardrailProfile.Strict);

        Assert.True(adjusted.Guardrails.IsExecutionBlocked);
        Assert.Equal(KubeActionConfirmationLevel.TypedConfirmationWithScope, adjusted.Guardrails.ConfirmationLevel);
        Assert.Contains("Strict", adjusted.Guardrails.Reasons.Last(), StringComparison.Ordinal);
        Assert.Contains("strict guardrail profile", adjusted.Guardrails.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StandardProfile_LeavesPreviewGuardrailsUnchanged()
    {
        var preview = CreatePreview(
            confirmationLevel: KubeActionConfirmationLevel.InlineSummary,
            blocked: false,
            riskLevel: KubeActionRiskLevel.Low,
            confidence: KubeActionPreviewConfidence.High);

        var adjusted = KubeActionGuardrailProfileAdjuster.Apply(preview, KubeActionGuardrailProfile.Standard);

        Assert.Equal(preview, adjusted);
    }

    private static KubeActionPreviewResponse CreatePreview(
        KubeActionConfirmationLevel confirmationLevel,
        bool blocked,
        KubeActionRiskLevel riskLevel,
        KubeActionPreviewConfidence confidence)
    {
        return new KubeActionPreviewResponse(
            Action: KubeActionKind.ScaleDeployment,
            Resource: new KubeResourceIdentity("kind-kuberkynesis-lab", KubeResourceKind.Deployment, "orders-prod", "orders-api"),
            Summary: "Preview summary",
            Confidence: confidence,
            Guardrails: new KubeActionGuardrailDecision(
                RiskLevel: riskLevel,
                ConfirmationLevel: confirmationLevel,
                IsExecutionBlocked: blocked,
                Summary: "Original summary",
                AcknowledgementHint: null,
                Reasons:
                [
                    "Original reason"
                ]),
            CoverageSummary: "Current-state coverage is partial.",
            Facts: [],
            Warnings: [],
            Notes: [],
            SaferAlternatives: [],
            AffectedResources: [],
            TransparencyCommands: []);
    }
}
