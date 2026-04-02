namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubeActionExecuteResponse(
    KubeActionKind Action,
    KubeResourceIdentity Resource,
    string Summary,
    DateTimeOffset CompletedAtUtc,
    IReadOnlyList<KubeActionPreviewFact> Facts,
    IReadOnlyList<string> Notes,
    IReadOnlyList<KubectlCommandPreview> TransparencyCommands)
{
    public KubeActionExecutionStatus Status { get; init; } = KubeActionExecutionStatus.Succeeded;

    public int RequestedTargetCount { get; init; } = 1;

    public int AttemptedTargetCount { get; init; } = 1;

    public int SucceededTargetCount { get; init; } = 1;

    public int FailedTargetCount { get; init; }

    public int SkippedTargetCount { get; init; }

    public IReadOnlyList<KubeActionExecutionTargetResult> TargetResults { get; init; } = [];
}
