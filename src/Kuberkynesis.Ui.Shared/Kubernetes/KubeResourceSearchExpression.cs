namespace Kuberkynesis.Ui.Shared.Kubernetes;

public static class KubeResourceSearchExpression
{
    public static IReadOnlyList<string> ParseAnyTerms(string? searchExpression)
    {
        if (string.IsNullOrWhiteSpace(searchExpression))
        {
            return [];
        }

        return searchExpression
            .Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(term => term.Trim())
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string? BuildAnyExpression(IEnumerable<string>? terms)
    {
        if (terms is null)
        {
            return null;
        }

        var normalizedTerms = terms
            .Select(term => term?.Trim())
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToArray();

        return normalizedTerms.Length is 0
            ? null
            : string.Join(" | ", normalizedTerms);
    }
}
