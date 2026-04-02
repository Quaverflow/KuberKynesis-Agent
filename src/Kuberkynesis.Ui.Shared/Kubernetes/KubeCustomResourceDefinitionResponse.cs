namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubeCustomResourceDefinitionResponse(
    IReadOnlyList<string> Contexts,
    IReadOnlyList<KubeCustomResourceType> Definitions,
    IReadOnlyList<KubeQueryWarning> Warnings,
    IReadOnlyList<KubectlCommandPreview>? TransparencyCommands = null);
