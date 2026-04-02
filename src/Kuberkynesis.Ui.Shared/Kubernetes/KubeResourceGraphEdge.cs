namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubeResourceGraphEdge(
    string FromNodeId,
    string ToNodeId,
    string Relationship);
