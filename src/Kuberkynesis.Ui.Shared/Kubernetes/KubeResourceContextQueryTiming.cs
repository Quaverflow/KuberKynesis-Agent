namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubeResourceContextQueryTiming(
    string ContextName,
    string TransportKind,
    int ClientAcquireMilliseconds,
    int QueryMilliseconds,
    int FilterMilliseconds,
    int ReturnedResourceCount,
    int MatchedResourceCount,
    bool TimedOut = false,
    string? Error = null);
