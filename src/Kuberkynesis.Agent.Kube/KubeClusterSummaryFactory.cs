using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Kube;

internal static class KubeClusterSummaryFactory
{
    private const string UnknownClusterName = "Unknown cluster";

    public static IReadOnlyList<KubeClusterSummary> Build(IEnumerable<KubeContextSummary> contexts)
    {
        ArgumentNullException.ThrowIfNull(contexts);

        return contexts
            .GroupBy(context => NormalizeClusterName(context.ClusterName), StringComparer.OrdinalIgnoreCase)
            .Select(static group =>
            {
                var orderedContexts = group
                    .OrderByDescending(static context => context.IsCurrent)
                    .ThenBy(static context => context.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                return new KubeClusterSummary(
                    Name: group.Key,
                    Server: orderedContexts
                        .Select(static context => NormalizeOptionalText(context.Server))
                        .FirstOrDefault(static server => !string.IsNullOrWhiteSpace(server)),
                    ContainsCurrentContext: orderedContexts.Any(static context => context.IsCurrent),
                    ContextCount: orderedContexts.Length,
                    QueryableContextCount: orderedContexts.Count(static context => context.Status is KubeContextStatus.Configured),
                    ContextNames: orderedContexts.Select(static context => context.Name).ToArray());
            })
            .OrderByDescending(static cluster => cluster.ContainsCurrentContext)
            .ThenBy(static cluster => cluster.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string NormalizeClusterName(string? clusterName)
    {
        return string.IsNullOrWhiteSpace(clusterName)
            ? UnknownClusterName
            : clusterName.Trim();
    }

    private static string? NormalizeOptionalText(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }
}
