namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubeResourceGraphResponse(
    string RootNodeId,
    IReadOnlyList<KubeResourceGraphNode> Nodes,
    IReadOnlyList<KubeResourceGraphEdge> Edges,
    IReadOnlyList<KubeQueryWarning> Warnings,
    IReadOnlyList<KubectlCommandPreview>? TransparencyCommands = null);
