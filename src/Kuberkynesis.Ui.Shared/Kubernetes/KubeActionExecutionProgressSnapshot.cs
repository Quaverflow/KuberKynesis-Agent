namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubeActionExecutionProgressSnapshot(
    string ExecutionId,
    KubeActionKind Action,
    KubeResourceIdentity Resource,
    DateTimeOffset UpdatedAtUtc,
    string StatusText,
    string Summary)
{
    public bool CanCancel { get; init; } = true;
}
