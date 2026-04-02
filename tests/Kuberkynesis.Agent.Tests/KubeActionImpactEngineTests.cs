using Kuberkynesis.Agent.Kube;
using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Tests;

public sealed class KubeActionImpactEngineTests
{
    [Fact]
    public void Attach_AddsStructuredImpactReportToPreview()
    {
        var engine = new KubeActionImpactEngine();
        var preview = new KubeActionPreviewResponse(
            Action: KubeActionKind.DeletePod,
            Resource: new KubeResourceIdentity("kind-kuberkynesis-lab", KubeResourceKind.Pod, "orders-prod", "orders-api-abc123"),
            Summary: "Pod/orders-api-abc123 would be deleted, and its controller would likely create a replacement.",
            Confidence: KubeActionPreviewConfidence.High,
            Guardrails: new KubeActionGuardrailDecision(
                RiskLevel: KubeActionRiskLevel.Medium,
                ConfirmationLevel: KubeActionConfirmationLevel.TypedConfirmation,
                IsExecutionBlocked: false,
                Summary: "Execution requires typed acknowledgement of the exact target.",
                AcknowledgementHint: "Type pod/orders-api-abc123 to confirm later.",
                Reasons: []),
            CoverageSummary: "Current-state coverage is strong for the current target and controller evidence.",
            Facts: [],
            Warnings: [],
            Notes: [],
            SaferAlternatives: [],
            AffectedResources:
            [
                new KubeRelatedResource("Current pod", KubeResourceKind.Pod, "v1", "orders-api-abc123", "orders-prod", "Running", "10.244.0.15"),
                new KubeRelatedResource("Immediate owner", KubeResourceKind.ReplicaSet, "apps/v1", "orders-api-rs", "orders-prod", null, null),
                new KubeRelatedResource("Uses Secret", KubeResourceKind.Secret, "v1", "orders-api-secrets", "orders-prod", null, null)
            ],
            TransparencyCommands:
            [
                new KubectlCommandPreview(
                    Label: "Delete pod preview",
                    Command: "kubectl --context kind-kuberkynesis-lab -n orders-prod delete pod/orders-api-abc123")
            ])
        {
            PermissionBlockers =
            [
                new KubeActionPermissionBlocker(
                    Scope: "Pod/orders-api-abc123 in namespace orders-prod on kind-kuberkynesis-lab",
                    Summary: "Kubernetes RBAC denied this action for the requested target.",
                    Detail: "pods/delete is not allowed in orders-prod")
            ]
        };

        var attached = engine.Attach(preview);

        Assert.NotNull(attached.ImpactReport);
        Assert.Equal(preview.Summary, attached.ImpactReport!.ActionSummary);
        Assert.Contains(attached.ImpactReport.DirectTargets, target => target.Relationship == "Direct target");
        Assert.Contains(attached.ImpactReport.IndirectImpacts, target => target.Relationship == "Immediate owner");
        Assert.Contains(attached.ImpactReport.SharedDependencies, target => target.Relationship == "Uses Secret");
        Assert.Single(attached.ImpactReport.PermissionBlockers);
        Assert.Single(attached.ImpactReport.EquivalentOperations);
    }
}
