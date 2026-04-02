namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubeLiveSurfaceQueryRequest
{
    public string ContextName { get; init; } = string.Empty;

    public KubeResourceKind Kind { get; init; }

    public string? Namespace { get; init; }

    public string Name { get; init; } = string.Empty;

    public int Limit { get; init; } = 20;
}
