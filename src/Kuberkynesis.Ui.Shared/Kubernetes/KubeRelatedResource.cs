namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubeRelatedResource(
    string Relationship,
    KubeResourceKind? Kind,
    string ApiVersion,
    string Name,
    string? Namespace,
    string? Status,
    string? Summary);
