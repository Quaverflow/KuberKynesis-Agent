namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed class KubeResourceWatchRequest
{
    public KubeResourceKind Kind { get; init; } = KubeResourceKind.Pod;

    public IReadOnlyList<string> Contexts { get; init; } = [];

    public string? Namespace { get; init; }

    public string? Search { get; init; }

    public int Limit { get; init; } = 100;
}
