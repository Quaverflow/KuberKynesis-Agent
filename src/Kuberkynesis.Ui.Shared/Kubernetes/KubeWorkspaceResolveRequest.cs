using System.ComponentModel.DataAnnotations;

namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubeWorkspaceResolveRequest
{
    [Required]
    public KubeResourceKind Kind { get; init; } = KubeResourceKind.Pod;

    public KubeCustomResourceType? CustomResourceType { get; init; }

    public bool IncludeAllSupportedKinds { get; init; }

    public IReadOnlyList<string> Contexts { get; init; } = [];

    public string? Namespace { get; init; }

    public string? Search { get; init; }

    [Range(1, 500)]
    public int Limit { get; init; } = 200;
}
