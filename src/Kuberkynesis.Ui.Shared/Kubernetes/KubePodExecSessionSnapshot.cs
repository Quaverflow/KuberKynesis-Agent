namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubePodExecSessionSnapshot(
    string SessionId,
    KubeResourceIdentity Resource,
    string? ContainerName,
    IReadOnlyList<string> Command,
    DateTimeOffset UpdatedAtUtc,
    string StatusText,
    string Summary,
    IReadOnlyList<KubePodExecOutputFrame> OutputFrames)
{
    public bool CanSendInput { get; init; } = true;

    public bool CanCancel { get; init; } = true;
}
