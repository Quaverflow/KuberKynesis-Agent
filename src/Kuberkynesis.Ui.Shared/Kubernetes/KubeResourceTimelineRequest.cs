namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubeResourceTimelineRequest
{
    public required string ContextName { get; init; }

    public required KubeResourceKind Kind { get; init; }

    public string? Namespace { get; init; }

    public required string Name { get; init; }
}
