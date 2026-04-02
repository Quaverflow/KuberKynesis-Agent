namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubeActionLocalEnvironmentRules(
    IReadOnlyList<string> ProductionMatchers,
    IReadOnlyList<string> StagingMatchers,
    IReadOnlyList<string> DevelopmentMatchers)
{
    public static readonly KubeActionLocalEnvironmentRules Empty = new([], [], []);

    public int TotalCount => ProductionMatchers.Count + StagingMatchers.Count + DevelopmentMatchers.Count;
}
