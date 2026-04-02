namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubeActionExecutionStreamMessage(
    KubeActionExecutionStreamMessageType MessageType,
    DateTimeOffset OccurredAtUtc,
    string ExecutionId,
    KubeActionExecutionProgressSnapshot? Snapshot,
    KubeActionExecuteResponse? Result,
    string? ErrorMessage,
    string? ErrorGuidance = null);
