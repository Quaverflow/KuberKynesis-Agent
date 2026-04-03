using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Core.Configuration;

public sealed class AgentRuntimeOptions
{
    public const string SectionName = "AgentRuntime";
    public const string DefaultPublicUrl = "http://127.0.0.1:46321";

    public string PublicUrl { get; set; } = DefaultPublicUrl;

    public string? AdvertisedVersionOverride { get; set; }

    public OriginOptions Origins { get; set; } = new();

    public PairingOptions Pairing { get; set; } = new();

    public PreviewReadOnlyLimitsOptions PreviewReadOnlyLimits { get; set; } = new();

    public ResourceQueryOptions ResourceQueries { get; set; } = new();

    public MetricsOptions Metrics { get; set; } = new();

    public UiLaunchOptions UiLaunch { get; set; } = new();
}

public sealed class OriginOptions
{
    public List<string> Interactive { get; set; } = [];

    public string PreviewPattern { get; set; } = string.Empty;
}

public sealed class PairingOptions
{
    public int CodeLength { get; set; } = 6;

    public int NonceLifetimeSeconds { get; set; } = 60;

    public int PairingCodeRotationMinutes { get; set; } = 10;

    public int SessionLifetimeHours { get; set; } = 8;

    public int MaxInteractiveSessions { get; set; } = 1;

    public int WebSocketTicketLifetimeSeconds { get; set; } = 60;

    public int DisconnectReleaseGraceSeconds { get; set; } = 30;
}

public sealed class PreviewReadOnlyLimitsOptions
{
    public int MaxConcurrentStreams { get; set; } = 8;

    public int MaxWatchCountPerSession { get; set; } = 12;

    public int MaxLogStreamsPerSession { get; set; } = 4;
}

public sealed class ResourceQueryOptions
{
    public int ContextTimeoutSeconds { get; set; } = 4;
}

public sealed class MetricsOptions
{
    public KubeMetricsSourceMode SourceMode { get; set; } = KubeMetricsSourceMode.MetricsApiPreferred;

    public PrometheusMetricsOptions Prometheus { get; set; } = new();
}

public sealed class PrometheusMetricsOptions
{
    public bool Enabled { get; set; }

    public string BaseUrl { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 5;

    public string CpuRateWindow { get; set; } = "5m";
}

public sealed class UiLaunchOptions
{
    public const string HostedProductionUrl = "https://kuberkynesis.com/";
    public const string LocalDevelopmentUrl = "http://localhost:5173/";

    public bool AutoOpenBrowser { get; set; } = true;

    public bool AutoConnectWithPairingCode { get; set; } = true;

    public string Url { get; set; } = HostedProductionUrl;

    public int ReadyTimeoutSeconds { get; set; } = 25;
}
