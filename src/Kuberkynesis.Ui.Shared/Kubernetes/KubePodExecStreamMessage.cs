namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubePodExecStreamMessage(
    KubePodExecStreamMessageType MessageType,
    DateTimeOffset OccurredAtUtc,
    string SessionId,
    KubePodExecSessionSnapshot? Snapshot,
    KubePodExecOutputFrame? OutputFrame,
    string? ErrorMessage,
    string? ErrorGuidance = null);
