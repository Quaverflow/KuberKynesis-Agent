namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubePodExecOutputFrame(
    DateTimeOffset OccurredAtUtc,
    KubePodExecOutputChannel Channel,
    string Text);
