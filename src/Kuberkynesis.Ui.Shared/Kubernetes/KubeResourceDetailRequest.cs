using System.ComponentModel.DataAnnotations;

namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubeResourceDetailRequest
{
    [Required]
    public string ContextName { get; init; } = string.Empty;

    [Required]
    public KubeResourceKind Kind { get; init; } = KubeResourceKind.Pod;

    public KubeCustomResourceType? CustomResourceType { get; init; }

    [Required]
    public string Name { get; init; } = string.Empty;

    public string? Namespace { get; init; }
}
