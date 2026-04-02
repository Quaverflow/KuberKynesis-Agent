namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubeActionExecutionTargetResult(
    KubeResourceIdentity Resource,
    KubeActionExecutionStatus Status,
    string? Reason);
