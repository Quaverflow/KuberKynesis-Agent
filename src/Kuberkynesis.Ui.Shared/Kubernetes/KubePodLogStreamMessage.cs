namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubePodLogStreamMessage(
    KubePodLogStreamMessageType MessageType,
    DateTimeOffset OccurredAtUtc,
    KubePodLogResponse? Snapshot,
    string? AppendContent,
    string? ErrorMessage,
    int? BatchedLineCount = null);
