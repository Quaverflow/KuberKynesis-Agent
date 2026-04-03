namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubeResourceQueryPerformance(
    int TotalMilliseconds,
    int KubeConfigLoadMilliseconds,
    int ContextResolutionMilliseconds,
    int OrderingMilliseconds,
    IReadOnlyList<KubeResourceContextQueryTiming> Contexts);
