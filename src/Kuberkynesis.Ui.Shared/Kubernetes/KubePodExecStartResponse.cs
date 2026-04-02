namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubePodExecStartResponse(
    string SessionId,
    KubeResourceIdentity Resource,
    string? ContainerName,
    IReadOnlyList<string> Command,
    DateTimeOffset StartedAtUtc,
    string StatusText,
    string Summary,
    IReadOnlyList<KubectlCommandPreview> TransparencyCommands)
{
    public bool CanSendInput { get; init; } = true;

    public bool CanCancel { get; init; } = true;
}
