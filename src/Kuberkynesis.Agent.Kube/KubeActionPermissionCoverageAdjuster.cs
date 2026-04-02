using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Kube;

internal static class KubeActionPermissionCoverageAdjuster
{
    private const string GenericWarning = "Preview coverage is partial because Kubernetes RBAC limited visibility into part of the affected scope.";
    private const string GenericPartialSummary = "Current-state coverage is partial because some affected scope could not be inspected under current RBAC.";

    public static KubeActionPreviewResponse Apply(
        KubeActionPreviewResponse preview,
        KubeActionPreviewPermissionCoverage coverage)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(coverage);

        if (!coverage.HasRestrictions)
        {
            return preview;
        }

        var facts = preview.Facts
            .Select(fact => coverage.FactOverrides.TryGetValue(fact.Label, out var replacement)
                ? fact with { Value = replacement }
                : fact)
            .ToArray();

        var warnings = preview.Warnings
            .Concat([GenericWarning])
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var coverageLimits = preview.CoverageLimits
            .Concat(coverage.PermissionBlockers
                .Select(static blocker => string.IsNullOrWhiteSpace(blocker.Detail) ? blocker.Summary : blocker.Detail!))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var reasons = preview.Guardrails.Reasons
            .Concat(["Preview scope is partially hidden by Kubernetes RBAC, so some affected-scope evidence is incomplete."])
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return preview with
        {
            Confidence = LowerConfidence(preview.Confidence),
            CoverageSummary = BuildCoverageSummary(preview.CoverageSummary),
            Facts = facts,
            Warnings = warnings,
            CoverageLimits = coverageLimits,
            Guardrails = preview.Guardrails with
            {
                Reasons = reasons
            },
            PermissionBlockers = preview.PermissionBlockers
                .Concat(coverage.PermissionBlockers)
                .Distinct()
                .ToArray()
        };
    }

    private static KubeActionPreviewConfidence LowerConfidence(KubeActionPreviewConfidence confidence)
    {
        return confidence switch
        {
            KubeActionPreviewConfidence.High => KubeActionPreviewConfidence.Medium,
            _ => confidence
        };
    }

    private static string BuildCoverageSummary(string existingSummary)
    {
        if (string.IsNullOrWhiteSpace(existingSummary))
        {
            return GenericPartialSummary;
        }

        if (existingSummary.Contains("strong", StringComparison.OrdinalIgnoreCase))
        {
            return GenericPartialSummary;
        }

        if (existingSummary.Contains("partial", StringComparison.OrdinalIgnoreCase))
        {
            return existingSummary;
        }

        return $"{existingSummary} Coverage is partial because some affected scope could not be inspected under current RBAC.";
    }
}
