using Kuberkynesis.Agent.Core.Configuration;
using Kuberkynesis.Agent.Core.Security;
using Kuberkynesis.Agent.Kube;
using Kuberkynesis.Ui.Shared.Connection;
using Kuberkynesis.Ui.Shared.Kubernetes;
using System.Net;

namespace Kuberkynesis.Agent.Transport.Api;

public sealed class AgentDiagnosticsResponseFactory
{
    public AgentDiagnosticsResponse Create(
        AgentRuntimeOptions options,
        PairingSessionRegistry sessions,
        KubeBootstrapProbeResult probe,
        IReadOnlyList<KubeContextSummary> contexts)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(contexts);

        var queryableContextCount = contexts.Count(static context => context.Status is KubeContextStatus.Configured);
        var authenticationExpiredContexts = contexts
            .Where(static context => context.Status is KubeContextStatus.AuthenticationExpired)
            .Select(static context => context.Name)
            .ToArray();
        var configurationErrorContexts = contexts
            .Where(static context => context.Status is KubeContextStatus.ConfigurationError)
            .Select(static context => context.Name)
            .ToArray();
        var issues = BuildIssues(
            probe,
            contexts.Count,
            queryableContextCount,
            authenticationExpiredContexts,
            configurationErrorContexts);

        return new AgentDiagnosticsResponse(
            AgentInstanceId: sessions.AgentInstanceId,
            AgentVersion: sessions.AgentVersion,
            PublicUrl: options.PublicUrl,
            UiLaunchUrl: options.UiLaunch.Url,
            BrowserAutoOpenEnabled: options.UiLaunch.AutoOpenBrowser,
            KubeConfigAvailable: probe.KubeConfigAvailable,
            KubectlAvailable: probe.KubectlAvailable,
            KubectlClientVersion: probe.KubectlClientVersion,
            MetricsSourceMode: options.Metrics.SourceMode,
            PrometheusEnabled: options.Metrics.Prometheus.Enabled,
            PrometheusBaseUrl: string.IsNullOrWhiteSpace(options.Metrics.Prometheus.BaseUrl)
                ? null
                : options.Metrics.Prometheus.BaseUrl,
            CurrentContextName: probe.CurrentContextName,
            ContextCount: probe.ContextCount,
            QueryableContextCount: queryableContextCount,
            AuthenticationExpiredContextCount: authenticationExpiredContexts.Length,
            ConfigurationErrorContextCount: configurationErrorContexts.Length,
            SourcePaths: probe.SourcePaths,
            InteractiveOrigins: options.Origins.Interactive,
            PreviewOriginPattern: options.Origins.PreviewPattern,
            Issues: issues,
            Warnings: probe.Warnings,
            TrustBoundary: BuildTrustBoundarySummary(options));
    }

    private static AgentTrustBoundarySummary BuildTrustBoundarySummary(AgentRuntimeOptions options)
    {
        var loopbackOnlyBinding = IsLoopbackUrl(options.PublicUrl);

        return new AgentTrustBoundarySummary(
            LoopbackOnlyBinding: loopbackOnlyBinding,
            KubeconfigUploadEnabled: false,
            RuntimeCloudSyncEnabled: false,
            RemoteExecutionEnabled: true,
            PublishedShareEnabled: false,
            SecretRevealEnabled: false,
            BindingSummary: loopbackOnlyBinding
                ? "The local agent binds to loopback by default, so the browser talks to a machine-local control point instead of a network-reachable backend."
                : $"The configured agent URL '{options.PublicUrl}' is not loopback-only. That weakens the default local-only trust boundary and should be treated as an explicit operator override.",
            ClusterAuthoritySummary: "Kubeconfig files, cluster credentials, and cluster authority stay on this machine. Shared views recreate perspective only and never recreate authority.",
            RuntimeDataSummary: "Diagnostics, live logs, Live Surface streams, action execution, and interactive exec shells stay local to the paired browser and local agent. They do not flow through remote sync or published sharing in this milestone.",
            SharingSummary: "Portable setup export stays descriptive only. Published share links and remote sync remain disabled in the current local-only milestone.",
            SecretHandlingSummary: "The UI does not reveal secret values. Secret-like text is redacted before browser persistence or export, and interactive exec remains an explicit opt-in shell transport rather than a secret reveal shortcut.");
    }

    private static bool IsLoopbackUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address);
    }

    private static IReadOnlyList<AgentDiagnosticsIssue> BuildIssues(
        KubeBootstrapProbeResult probe,
        int discoveredContextCount,
        int queryableContextCount,
        IReadOnlyList<string> authenticationExpiredContexts,
        IReadOnlyList<string> configurationErrorContexts)
    {
        var issues = new List<AgentDiagnosticsIssue>();

        if (!probe.KubeConfigAvailable)
        {
            var missingKubeConfigWarning = probe.Warnings.FirstOrDefault(static warning =>
                warning.StartsWith("No kubeconfig file was found.", StringComparison.Ordinal));
            var loadFailureWarning = probe.Warnings.FirstOrDefault(static warning =>
                warning.StartsWith("Unable to load kubeconfig:", StringComparison.Ordinal));

            if (!string.IsNullOrWhiteSpace(missingKubeConfigWarning))
            {
                issues.Add(new AgentDiagnosticsIssue(
                    AgentDiagnosticsIssueKind.MissingKubeConfig,
                    missingKubeConfigWarning,
                    []));
            }
            else if (!string.IsNullOrWhiteSpace(loadFailureWarning))
            {
                issues.Add(new AgentDiagnosticsIssue(
                    AgentDiagnosticsIssueKind.KubeConfigLoadFailed,
                    loadFailureWarning,
                    []));
            }
        }

        if (!probe.KubectlAvailable)
        {
            var kubectlWarning = probe.Warnings.FirstOrDefault(static warning =>
                warning.Contains("kubectl", StringComparison.OrdinalIgnoreCase))
                ?? "kubectl is not available to the agent startup environment.";

            issues.Add(new AgentDiagnosticsIssue(
                AgentDiagnosticsIssueKind.KubectlUnavailable,
                kubectlWarning,
                []));
        }

        if (authenticationExpiredContexts.Count > 0)
        {
            issues.Add(new AgentDiagnosticsIssue(
                AgentDiagnosticsIssueKind.AuthenticationExpired,
                authenticationExpiredContexts.Count is 1
                    ? $"The context '{authenticationExpiredContexts[0]}' needs fresh credentials."
                    : $"{authenticationExpiredContexts.Count} contexts need fresh credentials before they can be queried.",
                authenticationExpiredContexts.ToArray()));
        }

        if (configurationErrorContexts.Count > 0)
        {
            issues.Add(new AgentDiagnosticsIssue(
                AgentDiagnosticsIssueKind.ConfigurationError,
                configurationErrorContexts.Count is 1
                    ? $"The context '{configurationErrorContexts[0]}' has a kubeconfig configuration problem."
                    : $"{configurationErrorContexts.Count} contexts have kubeconfig configuration problems.",
                configurationErrorContexts.ToArray()));
        }

        if (probe.KubeConfigAvailable && queryableContextCount is 0)
        {
            var summary = discoveredContextCount is 0
                ? "The resolved kubeconfig files did not expose any contexts."
                : "The agent found kubeconfig entries, but none are currently queryable.";

            var affectedContexts = authenticationExpiredContexts
                .Concat(configurationErrorContexts)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            issues.Add(new AgentDiagnosticsIssue(
                AgentDiagnosticsIssueKind.NoQueryableContexts,
                summary,
                affectedContexts));
        }

        return issues;
    }
}
