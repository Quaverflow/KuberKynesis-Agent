using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Kube;

internal sealed record KubeActionPolicyContext(
    KubeActionKind Action,
    KubeResourceIdentity Resource,
    KubeActionEnvironmentKind Environment,
    KubeActionAvailability Availability,
    KubeActionPreviewConfidence Confidence,
    KubeActionRiskLevel BaseRiskLevel,
    IReadOnlyList<string> Reasons,
    bool HasSharedDependencies = false,
    bool DependencyImpactUnresolved = false,
    bool MultiResource = false,
    int DirectTargetCount = 1,
    int AffectedNamespaceCount = 1,
    bool IncludesSystemNamespaces = false,
    bool CanExecuteSafelyFromCurrentEvidence = true);
