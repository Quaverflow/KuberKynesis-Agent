namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubeActionExecuteRequest(
    string ContextName,
    KubeResourceKind Kind,
    string? Namespace,
    string Name,
    KubeActionKind Action,
    int? TargetReplicas,
    string? ConfirmationText,
    KubeActionGuardrailProfile GuardrailProfile = KubeActionGuardrailProfile.Standard,
    KubeActionLocalEnvironmentRules? LocalEnvironmentRules = null);
