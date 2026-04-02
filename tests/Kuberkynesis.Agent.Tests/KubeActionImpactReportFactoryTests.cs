using Kuberkynesis.Agent.Kube;
using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Tests;

public sealed class KubeActionImpactReportFactoryTests
{
    [Fact]
    public void Build_GroupsDirectTargetsIndirectImpactsAndSharedDependencies()
    {
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
                new KubeRelatedResource("Matched PDB", null, "PodDisruptionBudget", "orders-api-pdb", "orders-prod", "1 disruptions allowed", "2/1 healthy"),
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

        var report = KubeActionImpactReportFactory.Build(preview);

        Assert.Equal(preview.Summary, report.ActionSummary);
        Assert.Equal(2, report.DirectTargets.Count);
        Assert.Contains(report.DirectTargets, target => target.Relationship == "Direct target" && target.Name == "orders-api-abc123");
        Assert.Contains(report.DirectTargets, target => target.Relationship == "Current pod" && target.Name == "orders-api-abc123");
        Assert.Equal(2, report.IndirectImpacts.Count);
        Assert.Contains(report.IndirectImpacts, target => target.Relationship == "Immediate owner");
        Assert.Contains(report.IndirectImpacts, target => target.Relationship == "Matched PDB");
        Assert.Single(report.SharedDependencies);
        Assert.Equal("Uses Secret", report.SharedDependencies[0].Relationship);
        Assert.Single(report.PermissionBlockers);
        Assert.Single(report.EquivalentOperations);
        Assert.Equal("Delete pod preview", report.EquivalentOperations[0].Label);
    }
}
