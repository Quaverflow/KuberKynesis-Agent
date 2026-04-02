namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubePodLogResponse(
    string ContextName,
    string Namespace,
    string PodName,
    string? ContainerName,
    int TailLinesApplied,
    IReadOnlyList<string> AvailableContainers,
    string Content,
    IReadOnlyList<KubectlCommandPreview> TransparencyCommands,
    IReadOnlyList<KubeQueryWarning> Warnings);
