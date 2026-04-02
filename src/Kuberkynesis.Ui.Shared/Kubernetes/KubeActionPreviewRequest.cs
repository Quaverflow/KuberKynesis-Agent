namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubeActionPreviewRequest(
    string ContextName,
    KubeResourceKind Kind,
    string? Namespace,
    string Name,
    KubeActionKind Action,
    int? TargetReplicas = null,
    KubeActionGuardrailProfile GuardrailProfile = KubeActionGuardrailProfile.Standard,
    KubeActionLocalEnvironmentRules? LocalEnvironmentRules = null);
