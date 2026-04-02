namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubeActionExecutionStartResponse(
    string ExecutionId,
    KubeActionKind Action,
    KubeResourceIdentity Resource,
    DateTimeOffset StartedAtUtc,
    string StatusText,
    string Summary)
{
    public bool CanCancel { get; init; } = true;
}
