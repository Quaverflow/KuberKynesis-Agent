using k8s.Models;
using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Kube;

internal sealed record KubePodDisruptionBudgetImpact(
    IReadOnlyList<KubeRelatedResource> RelatedResources,
    int MatchedBudgetCount,
    int ZeroDisruptionsAllowedCount,
    int UnknownAllowanceCount)
{
    public bool HasMatchedBudgets => MatchedBudgetCount > 0;
}

internal static class KubePodDisruptionBudgetMatcher
{
    public static KubePodDisruptionBudgetImpact BuildImpact(
        IEnumerable<V1PodDisruptionBudget>? budgets,
        IEnumerable<V1Pod>? pods)
    {
        if (budgets is null || pods is null)
        {
            return Empty();
        }

        var podList = pods
            .Where(static pod => pod.Metadata?.Labels?.Count > 0)
            .ToArray();

        if (podList.Length is 0)
        {
            return Empty();
        }

        var relatedResources = new List<KubeRelatedResource>();
        var zeroDisruptionsAllowedCount = 0;
        var unknownAllowanceCount = 0;

        foreach (var budget in budgets)
        {
            if (!MatchesAnyPod(budget.Spec?.Selector, podList) ||
                string.IsNullOrWhiteSpace(budget.Metadata?.Name))
            {
                continue;
            }

            var disruptionsAllowed = budget.Status?.DisruptionsAllowed;
            if (disruptionsAllowed == 0)
            {
                zeroDisruptionsAllowedCount++;
            }
            else if (disruptionsAllowed is null)
            {
                unknownAllowanceCount++;
            }

            relatedResources.Add(new KubeRelatedResource(
                Relationship: "Matched PDB",
                Kind: null,
                ApiVersion: "PodDisruptionBudget",
                Name: budget.Metadata!.Name!,
                Namespace: budget.Metadata?.NamespaceProperty,
                Status: BuildStatusSummary(budget),
                Summary: BuildDetailSummary(budget)));
        }

        if (relatedResources.Count is 0)
        {
            return Empty();
        }

        return new KubePodDisruptionBudgetImpact(
            RelatedResources: relatedResources
                .OrderBy(static resource => resource.Namespace, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static resource => resource.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            MatchedBudgetCount: relatedResources.Count,
            ZeroDisruptionsAllowedCount: zeroDisruptionsAllowedCount,
            UnknownAllowanceCount: unknownAllowanceCount);
    }

    private static bool MatchesAnyPod(V1LabelSelector? selector, IReadOnlyList<V1Pod> pods)
    {
        return pods.Any(pod => MatchesSelector(selector, pod.Metadata?.Labels));
    }

    private static bool MatchesSelector(V1LabelSelector? selector, IDictionary<string, string>? labels)
    {
        if (selector is null)
        {
            return false;
        }

        var hasLabels = selector.MatchLabels?.Count > 0;
        var hasExpressions = selector.MatchExpressions?.Count > 0;

        if (!hasLabels && !hasExpressions)
        {
            return true;
        }

        IDictionary<string, string> effectiveLabels = labels ?? new Dictionary<string, string>(StringComparer.Ordinal);

        if (hasLabels)
        {
            foreach (var (key, value) in selector.MatchLabels!)
            {
                if (!effectiveLabels.TryGetValue(key, out var labelValue) ||
                    !string.Equals(labelValue, value, StringComparison.Ordinal))
                {
                    return false;
                }
            }
        }

        if (!hasExpressions)
        {
            return true;
        }

        foreach (var requirement in selector.MatchExpressions!)
        {
            if (!MatchesExpression(requirement, effectiveLabels))
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesExpression(
        V1LabelSelectorRequirement requirement,
        IDictionary<string, string> labels)
    {
        if (string.IsNullOrWhiteSpace(requirement.Key))
        {
            return false;
        }

        var key = requirement.Key;
        var values = requirement.Values ?? [];
        var hasValue = labels.TryGetValue(key, out var labelValue);

        return requirement.OperatorProperty switch
        {
            "In" => hasValue && values.Contains(labelValue!, StringComparer.Ordinal),
            "NotIn" => !hasValue || !values.Contains(labelValue!, StringComparer.Ordinal),
            "Exists" => hasValue,
            "DoesNotExist" => !hasValue,
            "Gt" => hasValue &&
                    TryParseSelectorNumber(values.FirstOrDefault(), out var gtValue) &&
                    int.TryParse(labelValue, out var numericLabelValue) &&
                    numericLabelValue > gtValue,
            "Lt" => hasValue &&
                    TryParseSelectorNumber(values.FirstOrDefault(), out var ltValue) &&
                    int.TryParse(labelValue, out var numericLabelValue) &&
                    numericLabelValue < ltValue,
            _ => false
        };
    }

    private static bool TryParseSelectorNumber(string? value, out int parsedValue)
    {
        return int.TryParse(value, out parsedValue);
    }

    private static string BuildStatusSummary(V1PodDisruptionBudget budget)
    {
        return budget.Status?.DisruptionsAllowed switch
        {
            int disruptionsAllowed => $"{disruptionsAllowed} disruptions allowed",
            _ => "allowance unknown"
        };
    }

    private static string BuildDetailSummary(V1PodDisruptionBudget budget)
    {
        var parts = new List<string>();

        if (budget.Status?.CurrentHealthy is int currentHealthy &&
            budget.Status?.DesiredHealthy is int desiredHealthy)
        {
            parts.Add($"{currentHealthy}/{desiredHealthy} healthy");
        }
        else if (budget.Status?.ExpectedPods is int expectedPods)
        {
            parts.Add($"{expectedPods} expected pods");
        }

        if (budget.Spec?.MinAvailable is not null)
        {
            parts.Add($"minAvailable {budget.Spec.MinAvailable}");
        }
        else if (budget.Spec?.MaxUnavailable is not null)
        {
            parts.Add($"maxUnavailable {budget.Spec.MaxUnavailable}");
        }

        return parts.Count is 0
            ? "Disruption constraints advertised for matching pods."
            : string.Join(" • ", parts);
    }

    private static KubePodDisruptionBudgetImpact Empty()
    {
        return new KubePodDisruptionBudgetImpact(
            RelatedResources: [],
            MatchedBudgetCount: 0,
            ZeroDisruptionsAllowedCount: 0,
            UnknownAllowanceCount: 0);
    }
}
