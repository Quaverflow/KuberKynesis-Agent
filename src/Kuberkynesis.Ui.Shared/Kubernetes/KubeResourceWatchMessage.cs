namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubeResourceWatchMessage(
    KubeResourceWatchMessageType MessageType,
    DateTimeOffset OccurredAtUtc,
    KubeResourceQueryResponse? Snapshot,
    string? ErrorMessage,
    int? CoalescedEventCount = null);
