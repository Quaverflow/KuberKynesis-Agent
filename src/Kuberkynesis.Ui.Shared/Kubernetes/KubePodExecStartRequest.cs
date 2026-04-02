namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubePodExecStartRequest(
    string ContextName,
    string Namespace,
    string PodName,
    IReadOnlyList<string> Command,
    string? ContainerName = null);
