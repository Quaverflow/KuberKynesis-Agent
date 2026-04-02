namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubeResourceDetailSection(
    string Title,
    IReadOnlyList<KubeResourceDetailField> Fields);
