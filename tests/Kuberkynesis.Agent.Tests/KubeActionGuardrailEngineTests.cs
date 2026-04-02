using Kuberkynesis.Agent.Kube;
using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Tests;

public sealed class KubeActionGuardrailEngineTests
{
    [Fact]
    public void Finalize_AppliesGuardrailProfileAndMergesDeniedExecutionAccess()
    {
        var engine = new KubeActionGuardrailEngine();
        var request = new KubeActionPreviewRequest(
            ContextName: "kind-kuberkynesis-lab",
            Kind: KubeResourceKind.Deployment,
            Namespace: "orders-prod",
            Name: "orders-api",
            Action: KubeActionKind.ScaleDeployment,
            TargetReplicas: 5,
            GuardrailProfile: KubeActionGuardrailProfile.Conservative);
        var preview = CreatePreview(
            confirmationLevel: KubeActionConfirmationLevel.ExplicitReview,
            blocked: false,
            riskLevel: KubeActionRiskLevel.Medium,
            existingBlockers:
            [
                new KubeActionPermissionBlocker(
                    Scope: "Matching pods in namespace orders-prod",
                    Summary: "Kubernetes RBAC limited preview visibility for the current workload scope.",
                    Detail: "pods/list is denied")
            ]);
        var executionAccess = new KubeActionExecutionAccess(
            State: KubeActionExecutionAccessState.Denied,
            Summary: "Kubernetes RBAC currently denies this action for the active identity.",
            Detail: "deployments/scale update is denied in orders-prod");

        var finalized = engine.Finalize(preview, request, executionAccess);

        Assert.Equal(KubeActionConfirmationLevel.TypedConfirmation, finalized.Guardrails.ConfirmationLevel);
        Assert.Contains("Conservative", finalized.Guardrails.Reasons.Last(), StringComparison.Ordinal);
        Assert.Equal(KubeActionExecutionAccessState.Denied, finalized.ExecutionAccess.State);
        Assert.Equal(2, finalized.PermissionBlockers.Count);
        Assert.Contains(
            finalized.PermissionBlockers,
            blocker => blocker.Scope == "Deployment/orders-api in namespace orders-prod on kind-kuberkynesis-lab" &&
                       blocker.Summary == "Kubernetes RBAC denied this action for the requested target.");
    }

    [Fact]
    public void Finalize_DoesNotAddExecutionPermissionBlockerWhenAccessIsAllowed()
    {
        var engine = new KubeActionGuardrailEngine();
        var request = new KubeActionPreviewRequest(
            ContextName: "kind-kuberkynesis-lab",
            Kind: KubeResourceKind.CronJob,
            Namespace: "orders-prod",
            Name: "orders-reconciliation",
            Action: KubeActionKind.SuspendCronJob);
        var preview = CreatePreview(
            confirmationLevel: KubeActionConfirmationLevel.InlineSummary,
            blocked: false,
            riskLevel: KubeActionRiskLevel.Low,
            existingBlockers: []);
        var executionAccess = new KubeActionExecutionAccess(
            State: KubeActionExecutionAccessState.Allowed,
            Summary: "Kubernetes RBAC currently allows this action for the active identity.",
            Detail: null);

        var finalized = engine.Finalize(preview, request, executionAccess);

        Assert.Equal(KubeActionExecutionAccessState.Allowed, finalized.ExecutionAccess.State);
        Assert.Empty(finalized.PermissionBlockers);
    }

    private static KubeActionPreviewResponse CreatePreview(
        KubeActionConfirmationLevel confirmationLevel,
        bool blocked,
        KubeActionRiskLevel riskLevel,
        IReadOnlyList<KubeActionPermissionBlocker> existingBlockers)
    {
        return new KubeActionPreviewResponse(
            Action: KubeActionKind.ScaleDeployment,
            Resource: new KubeResourceIdentity("kind-kuberkynesis-lab", KubeResourceKind.Deployment, "orders-prod", "orders-api"),
            Summary: "Preview summary",
            Confidence: KubeActionPreviewConfidence.Medium,
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
            TransparencyCommands: [])
        {
            PermissionBlockers = existingBlockers
        };
    }
}
