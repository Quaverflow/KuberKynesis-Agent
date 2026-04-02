namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubePodExecInputRequest(
    string Text,
    bool AppendNewline = true);
