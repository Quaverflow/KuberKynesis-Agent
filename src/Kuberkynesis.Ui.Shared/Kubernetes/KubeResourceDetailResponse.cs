namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubeResourceDetailResponse(
    KubeResourceSummary Resource,
    IReadOnlyList<KubeResourceDetailSection> Sections,
    IReadOnlyList<KubeRelatedResource> RelatedResources,
    IReadOnlyList<KubeQueryWarning> Warnings,
    string? RawJson = null,
    string? RawYaml = null,
    IReadOnlyList<KubectlCommandPreview>? TransparencyCommands = null);
