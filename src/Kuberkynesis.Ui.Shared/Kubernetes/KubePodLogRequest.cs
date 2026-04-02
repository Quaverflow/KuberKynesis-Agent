using System.ComponentModel.DataAnnotations;

namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubePodLogRequest
{
    [Required]
    public string ContextName { get; init; } = string.Empty;

    [Required]
    public string Namespace { get; init; } = string.Empty;

    [Required]
    public string PodName { get; init; } = string.Empty;

    public string? ContainerName { get; init; }

    public int TailLines { get; init; } = 200;
}
