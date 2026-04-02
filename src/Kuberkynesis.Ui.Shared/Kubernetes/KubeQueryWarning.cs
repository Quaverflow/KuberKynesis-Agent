namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubeQueryWarning(
    string? ContextName,
    string Message);
