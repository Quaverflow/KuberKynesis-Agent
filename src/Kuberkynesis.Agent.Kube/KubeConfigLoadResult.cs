using k8s.KubeConfigModels;

namespace Kuberkynesis.Agent.Kube;

public sealed record KubeConfigLoadResult(
    K8SConfiguration? Configuration,
    IReadOnlyList<FileInfo> SourcePaths,
    string? CurrentContextName,
    IReadOnlyList<DiscoveredKubeContext> Contexts,
    IReadOnlyList<string> Warnings);
