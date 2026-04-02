namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubeActionExecutionAccess(
    KubeActionExecutionAccessState State,
    string Summary,
    string? Detail);
