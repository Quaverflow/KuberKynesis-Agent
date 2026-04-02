using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kuberkynesis.Agent.Core.Configuration;

namespace Kuberkynesis.Agent.Kube;

public sealed class PrometheusMetricsSource
{
    private readonly HttpClient httpClient;
    private readonly PrometheusMetricsOptions options;

    public PrometheusMetricsSource(HttpClient httpClient, AgentRuntimeOptions runtimeOptions)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(runtimeOptions);

        this.httpClient = httpClient;
        options = runtimeOptions.Metrics.Prometheus;

        if (Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUri))
        {
            this.httpClient.BaseAddress = baseUri;
        }

        if (options.TimeoutSeconds > 0)
        {
            this.httpClient.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        }
    }

    public bool IsEnabled =>
        options.Enabled &&
        Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out _);

    public async Task<PrometheusPodMetricsResult> QueryPodUsageAsync(
        string namespaceName,
        IReadOnlyCollection<string> podNames,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        ArgumentNullException.ThrowIfNull(podNames);

        var normalizedPodNames = podNames
            .Where(static podName => !string.IsNullOrWhiteSpace(podName))
            .Select(static podName => podName.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (!IsEnabled)
        {
            return PrometheusPodMetricsResult.Disabled;
        }

        if (normalizedPodNames.Length is 0)
        {
            return PrometheusPodMetricsResult.Disabled;
        }

        try
        {
            var cpuQuery = BuildCpuQuery(namespaceName, normalizedPodNames);
            var memoryQuery = BuildMemoryQuery(namespaceName, normalizedPodNames);

            var cpuSeries = await ExecuteVectorQueryAsync(cpuQuery, cancellationToken);
            var memorySeries = await ExecuteVectorQueryAsync(memoryQuery, cancellationToken);

            var latestTimestamp = cpuSeries.Timestamp > memorySeries.Timestamp
                ? cpuSeries.Timestamp
                : memorySeries.Timestamp;

            var usageByPod = normalizedPodNames.ToDictionary(
                static podName => podName,
                podName =>
                {
                    cpuSeries.ValuesByPod.TryGetValue(podName, out var cpuValue);
                    memorySeries.ValuesByPod.TryGetValue(podName, out var memoryValue);
                    return new PrometheusPodUsage(cpuValue, memoryValue);
                },
                StringComparer.Ordinal);

            var metricsAvailable = usageByPod.Values.Any(static usage => usage.CpuMillicores.HasValue || usage.MemoryBytes.HasValue);

            return metricsAvailable
                ? new PrometheusPodMetricsResult(
                    MetricsAvailable: true,
                    CollectedAtUtc: latestTimestamp,
                    Window: $"Prometheus {options.CpuRateWindow} rate",
                    UsageByPod: usageByPod,
                    FailureMessage: null)
                : new PrometheusPodMetricsResult(
                    MetricsAvailable: false,
                    CollectedAtUtc: latestTimestamp,
                    Window: $"Prometheus {options.CpuRateWindow} rate",
                    UsageByPod: usageByPod,
                    FailureMessage: "Prometheus did not return pod usage for the requested scope.");
        }
        catch (Exception exception)
        {
            return new PrometheusPodMetricsResult(
                MetricsAvailable: false,
                CollectedAtUtc: null,
                Window: null,
                UsageByPod: new Dictionary<string, PrometheusPodUsage>(StringComparer.Ordinal),
                FailureMessage: $"Prometheus is configured but unavailable: {exception.Message}");
        }
    }

    private async Task<PrometheusVectorSeriesResult> ExecuteVectorQueryAsync(string query, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(
            $"api/v1/query?query={Uri.EscapeDataString(query)}",
            cancellationToken);

        response.EnsureSuccessStatusCode();

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);

        var root = document.RootElement;

        if (!string.Equals(root.GetProperty("status").GetString(), "success", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Prometheus did not return a successful response.");
        }

        var valuesByPod = new Dictionary<string, long>(StringComparer.Ordinal);
        DateTimeOffset? latestTimestamp = null;

        foreach (var result in root.GetProperty("data").GetProperty("result").EnumerateArray())
        {
            if (!result.TryGetProperty("metric", out var metricElement) ||
                !metricElement.TryGetProperty("pod", out var podElement))
            {
                continue;
            }

            var podName = podElement.GetString();

            if (string.IsNullOrWhiteSpace(podName) ||
                !result.TryGetProperty("value", out var valueElement) ||
                valueElement.ValueKind is not JsonValueKind.Array ||
                valueElement.GetArrayLength() < 2)
            {
                continue;
            }

            var timestamp = ParseTimestamp(valueElement[0]);
            var numericValue = ParseNumericValue(valueElement[1]);

            if (!numericValue.HasValue)
            {
                continue;
            }

            valuesByPod[podName] = (long)Math.Round(numericValue.Value, MidpointRounding.AwayFromZero);

            if (!latestTimestamp.HasValue || timestamp > latestTimestamp.Value)
            {
                latestTimestamp = timestamp;
            }
        }

        return new PrometheusVectorSeriesResult(valuesByPod, latestTimestamp);
    }

    private string BuildCpuQuery(string namespaceName, IReadOnlyCollection<string> podNames)
    {
        return $"sum by (pod) (rate(container_cpu_usage_seconds_total{{namespace=\"{EscapePromQlString(namespaceName)}\",{BuildPodSelector(podNames)},container!=\"\",container!=\"POD\",image!=\"\"}}[{EscapePromQlString(options.CpuRateWindow)}])) * 1000";
    }

    private string BuildMemoryQuery(string namespaceName, IReadOnlyCollection<string> podNames)
    {
        return $"sum by (pod) (container_memory_working_set_bytes{{namespace=\"{EscapePromQlString(namespaceName)}\",{BuildPodSelector(podNames)},container!=\"\",container!=\"POD\",image!=\"\"}})";
    }

    private static string BuildPodSelector(IReadOnlyCollection<string> podNames)
    {
        var pattern = $"^({string.Join("|", podNames.Select(Regex.Escape))})$";
        return $"pod=~\"{EscapePromQlString(pattern)}\"";
    }

    private static string EscapePromQlString(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private static DateTimeOffset ParseTimestamp(JsonElement element)
    {
        var seconds = element.ValueKind switch
        {
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.String when double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedSeconds) => parsedSeconds,
            _ => 0d
        };

        return DateTimeOffset.UnixEpoch.AddSeconds(seconds);
    }

    private static double? ParseNumericValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.String when double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedValue) => parsedValue,
            _ => null
        };
    }
}

public sealed record PrometheusPodMetricsResult(
    bool MetricsAvailable,
    DateTimeOffset? CollectedAtUtc,
    string? Window,
    IReadOnlyDictionary<string, PrometheusPodUsage> UsageByPod,
    string? FailureMessage)
{
    public static PrometheusPodMetricsResult Disabled { get; } = new(
        MetricsAvailable: false,
        CollectedAtUtc: null,
        Window: null,
        UsageByPod: new Dictionary<string, PrometheusPodUsage>(StringComparer.Ordinal),
        FailureMessage: null);
}

public sealed record PrometheusPodUsage(
    long? CpuMillicores,
    long? MemoryBytes);

internal sealed record PrometheusVectorSeriesResult(
    IReadOnlyDictionary<string, long> ValuesByPod,
    DateTimeOffset? Timestamp);
