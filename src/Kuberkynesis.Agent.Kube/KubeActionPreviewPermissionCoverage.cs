using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Kube;

internal sealed record KubeActionPreviewPermissionCoverage(
    IReadOnlyList<KubeActionPermissionBlocker> PermissionBlockers,
    IReadOnlyDictionary<string, string> FactOverrides)
{
    public static KubeActionPreviewPermissionCoverage Empty { get; } =
        new([], new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    public bool HasRestrictions => PermissionBlockers.Count > 0 || FactOverrides.Count > 0;

    public static KubeActionPreviewPermissionCoverage Create(
        KubeActionPermissionBlocker blocker,
        params KeyValuePair<string, string>[] factOverrides)
    {
        ArgumentNullException.ThrowIfNull(blocker);

        return new KubeActionPreviewPermissionCoverage(
            [blocker],
            factOverrides.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase));
    }

    public static KubeActionPreviewPermissionCoverage Combine(params KubeActionPreviewPermissionCoverage[] coverages)
    {
        if (coverages.Length is 0)
        {
            return Empty;
        }

        var blockers = new List<KubeActionPermissionBlocker>();
        var factOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var coverage in coverages)
        {
            if (coverage is null)
            {
                continue;
            }

            blockers.AddRange(coverage.PermissionBlockers);

            foreach (var pair in coverage.FactOverrides)
            {
                factOverrides[pair.Key] = pair.Value;
            }
        }

        return blockers.Count is 0 && factOverrides.Count is 0
            ? Empty
            : new KubeActionPreviewPermissionCoverage(blockers.Distinct().ToArray(), factOverrides);
    }
}
