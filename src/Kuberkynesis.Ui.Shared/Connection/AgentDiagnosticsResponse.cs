namespace Kuberkynesis.Ui.Shared.Connection;

using Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record AgentDiagnosticsResponse(
    string AgentInstanceId,
    string AgentVersion,
    string PublicUrl,
    string UiLaunchUrl,
    bool BrowserAutoOpenEnabled,
    bool KubeConfigAvailable,
    bool KubectlAvailable,
    string? KubectlClientVersion,
    KubeMetricsSourceMode MetricsSourceMode,
    bool PrometheusEnabled,
    string? PrometheusBaseUrl,
    string? CurrentContextName,
    int ContextCount,
    int QueryableContextCount,
    int AuthenticationExpiredContextCount,
    int ConfigurationErrorContextCount,
    IReadOnlyList<string> SourcePaths,
    IReadOnlyList<string> InteractiveOrigins,
    string PreviewOriginPattern,
    IReadOnlyList<AgentDiagnosticsIssue> Issues,
    IReadOnlyList<string> Warnings,
    AgentTrustBoundarySummary? TrustBoundary = null);
