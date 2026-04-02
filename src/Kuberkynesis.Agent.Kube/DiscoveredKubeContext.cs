using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Kube;

public sealed record DiscoveredKubeContext(
    string Name,
    bool IsCurrent,
    string? ClusterName,
    string? Namespace,
    string? UserName,
    string? Server,
    KubeContextStatus Status,
    string? StatusMessage);
