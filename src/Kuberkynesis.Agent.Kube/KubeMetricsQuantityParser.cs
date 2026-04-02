using System.Globalization;

namespace Kuberkynesis.Agent.Kube;

internal static class KubeMetricsQuantityParser
{
    public static long? ParseCpuMillicores(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();

        if (trimmed.EndsWith("n", StringComparison.OrdinalIgnoreCase) &&
            decimal.TryParse(trimmed[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var nanoCores))
        {
            return (long)Math.Round(nanoCores / 1_000_000m, MidpointRounding.AwayFromZero);
        }

        if (trimmed.EndsWith("u", StringComparison.OrdinalIgnoreCase) &&
            decimal.TryParse(trimmed[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var microCores))
        {
            return (long)Math.Round(microCores / 1_000m, MidpointRounding.AwayFromZero);
        }

        if (trimmed.EndsWith("m", StringComparison.OrdinalIgnoreCase) &&
            decimal.TryParse(trimmed[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var milliCores))
        {
            return (long)Math.Round(milliCores, MidpointRounding.AwayFromZero);
        }

        return decimal.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var cores)
            ? (long)Math.Round(cores * 1000m, MidpointRounding.AwayFromZero)
            : null;
    }

    public static long? ParseBytes(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        var suffix = GetBinaryOrDecimalSuffix(trimmed, out var numericPart);
        var multiplier = suffix switch
        {
            "Ki" => 1024m,
            "Mi" => 1024m * 1024m,
            "Gi" => 1024m * 1024m * 1024m,
            "Ti" => 1024m * 1024m * 1024m * 1024m,
            "Pi" => 1024m * 1024m * 1024m * 1024m * 1024m,
            "Ei" => 1024m * 1024m * 1024m * 1024m * 1024m * 1024m,
            "K" => 1000m,
            "M" => 1000m * 1000m,
            "G" => 1000m * 1000m * 1000m,
            "T" => 1000m * 1000m * 1000m * 1000m,
            "P" => 1000m * 1000m * 1000m * 1000m * 1000m,
            "E" => 1000m * 1000m * 1000m * 1000m * 1000m * 1000m,
            _ => 1m
        };

        return decimal.TryParse(numericPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var quantity)
            ? (long)Math.Round(quantity * multiplier, MidpointRounding.AwayFromZero)
            : null;
    }

    private static string GetBinaryOrDecimalSuffix(string value, out string numericPart)
    {
        foreach (var suffix in new[] { "Ki", "Mi", "Gi", "Ti", "Pi", "Ei", "K", "M", "G", "T", "P", "E" })
        {
            if (value.EndsWith(suffix, StringComparison.Ordinal))
            {
                numericPart = value[..^suffix.Length];
                return suffix;
            }
        }

        numericPart = value;
        return string.Empty;
    }
}
