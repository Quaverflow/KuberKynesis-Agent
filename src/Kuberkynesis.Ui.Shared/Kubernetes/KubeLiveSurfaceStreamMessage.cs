namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubeLiveSurfaceStreamMessage(
    KubeLiveSurfaceStreamMessageType MessageType,
    DateTimeOffset OccurredAtUtc,
    KubeLiveSurfaceQueryResponse? Snapshot,
    string? ErrorMessage,
    int? CoalescedEventCount = null);
