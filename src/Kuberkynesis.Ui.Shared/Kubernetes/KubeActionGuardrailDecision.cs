namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubeActionGuardrailDecision(
    KubeActionRiskLevel RiskLevel,
    KubeActionConfirmationLevel ConfirmationLevel,
    bool IsExecutionBlocked,
    string Summary,
    string? AcknowledgementHint,
    IReadOnlyList<string> Reasons);
