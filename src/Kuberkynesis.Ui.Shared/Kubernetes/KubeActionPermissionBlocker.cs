namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubeActionPermissionBlocker(
    string Scope,
    string Summary,
    string? Detail);
