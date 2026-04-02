using Kuberkynesis.Agent.Core.Configuration;
using Kuberkynesis.Agent.Core.Security;
using Kuberkynesis.Agent.Transport.Api;
using Kuberkynesis.Ui.Shared.Connection;
using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Tests;

public sealed class AgentDiagnosticsResponseFactoryTests
{
    [Fact]
    public void Create_MapsTheCurrentStartupAndProbeState()
    {
        var options = new AgentRuntimeOptions
        {
            PublicUrl = "http://127.0.0.1:46321",
            Origins = new OriginOptions
            {
                Interactive = ["http://localhost:5173"],
                PreviewPattern = "^https://preview.example$"
            },
            Metrics = new MetricsOptions
            {
                SourceMode = KubeMetricsSourceMode.PrometheusPreferred,
                Prometheus = new PrometheusMetricsOptions
                {
                    Enabled = true,
                    BaseUrl = "http://127.0.0.1:9099/"
                }
            },
            UiLaunch = new UiLaunchOptions
            {
                Url = "http://localhost:5173/",
                AutoOpenBrowser = false
            }
        };

        var sessions = new PairingSessionRegistry(options);
        var probe = new Kuberkynesis.Agent.Kube.KubeBootstrapProbeResult(
            KubeConfigAvailable: true,
            KubectlAvailable: true,
            KubectlClientVersion: "v1.34.1",
            CurrentContextName: "kind-kuberkynesis-lab",
            ContextCount: 1,
            SourcePaths: [@"C:\temp\lab.kubeconfig"],
            Warnings: []);

        var response = new AgentDiagnosticsResponseFactory().Create(options, sessions, probe,
        [
            new KubeContextSummary(
                Name: "kind-kuberkynesis-lab",
                IsCurrent: true,
                ClusterName: "kind-kuberkynesis-lab",
                Namespace: "default",
                UserName: "kind-kuberkynesis-lab",
                Server: "https://127.0.0.1:6443",
                Status: KubeContextStatus.Configured,
                StatusMessage: null)
        ]);

        Assert.Equal(sessions.AgentInstanceId, response.AgentInstanceId);
        Assert.Equal(sessions.AgentVersion, response.AgentVersion);
        Assert.Equal("http://127.0.0.1:46321", response.PublicUrl);
        Assert.False(response.BrowserAutoOpenEnabled);
        Assert.Equal("v1.34.1", response.KubectlClientVersion);
        Assert.Equal(KubeMetricsSourceMode.PrometheusPreferred, response.MetricsSourceMode);
        Assert.True(response.PrometheusEnabled);
        Assert.Equal("http://127.0.0.1:9099/", response.PrometheusBaseUrl);
        Assert.Single(response.SourcePaths);
        Assert.Single(response.InteractiveOrigins);
        Assert.Equal(1, response.QueryableContextCount);
        Assert.Empty(response.Issues);
        Assert.NotNull(response.TrustBoundary);
        Assert.True(response.TrustBoundary!.LoopbackOnlyBinding);
        Assert.False(response.TrustBoundary.KubeconfigUploadEnabled);
        Assert.False(response.TrustBoundary.RuntimeCloudSyncEnabled);
        Assert.True(response.TrustBoundary.RemoteExecutionEnabled);
        Assert.False(response.TrustBoundary.PublishedShareEnabled);
        Assert.False(response.TrustBoundary.SecretRevealEnabled);
        Assert.Contains("loopback", response.TrustBoundary.BindingSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("stay on this machine", response.TrustBoundary.ClusterAuthoritySummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_FlagsNonLoopbackAgentUrlsAsAnExplicitTrustBoundaryOverride()
    {
        var options = new AgentRuntimeOptions
        {
            PublicUrl = "http://10.20.30.40:46321",
            Origins = new OriginOptions
            {
                Interactive = ["http://localhost:5173"],
                PreviewPattern = "^https://preview.example$"
            }
        };

        var sessions = new PairingSessionRegistry(options);
        var probe = new Kuberkynesis.Agent.Kube.KubeBootstrapProbeResult(
            KubeConfigAvailable: true,
            KubectlAvailable: true,
            KubectlClientVersion: "v1.34.1",
            CurrentContextName: "kind-kuberkynesis-lab",
            ContextCount: 1,
            SourcePaths: [@"C:\temp\lab.kubeconfig"],
            Warnings: []);

        var response = new AgentDiagnosticsResponseFactory().Create(options, sessions, probe,
        [
            new KubeContextSummary(
                Name: "kind-kuberkynesis-lab",
                IsCurrent: true,
                ClusterName: "kind-kuberkynesis-lab",
                Namespace: "default",
                UserName: "kind-kuberkynesis-lab",
                Server: "https://127.0.0.1:6443",
                Status: KubeContextStatus.Configured,
                StatusMessage: null)
        ]);

        Assert.NotNull(response.TrustBoundary);
        Assert.False(response.TrustBoundary!.LoopbackOnlyBinding);
        Assert.Contains("10.20.30.40", response.TrustBoundary.BindingSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_BuildsStructuredSetupIssuesFromProbeAndContextStatus()
    {
        var options = new AgentRuntimeOptions
        {
            PublicUrl = "http://127.0.0.1:46321",
            Origins = new OriginOptions
            {
                Interactive = ["http://localhost:5173"],
                PreviewPattern = "^https://preview.example$"
            }
        };

        var sessions = new PairingSessionRegistry(options);
        var probe = new Kuberkynesis.Agent.Kube.KubeBootstrapProbeResult(
            KubeConfigAvailable: false,
            KubectlAvailable: false,
            KubectlClientVersion: null,
            CurrentContextName: null,
            ContextCount: 2,
            SourcePaths: [@"C:\temp\lab.kubeconfig"],
            Warnings:
            [
                "Unable to load kubeconfig: invalid document",
                "kubectl was not found on PATH."
            ]);

        var response = new AgentDiagnosticsResponseFactory().Create(options, sessions, probe,
        [
            new KubeContextSummary(
                Name: "lab-expired",
                IsCurrent: false,
                ClusterName: "lab",
                Namespace: "default",
                UserName: "lab-expired",
                Server: "https://127.0.0.1:6443",
                Status: KubeContextStatus.AuthenticationExpired,
                StatusMessage: "Stored credentials were rejected."),
            new KubeContextSummary(
                Name: "lab-broken",
                IsCurrent: false,
                ClusterName: "lab",
                Namespace: "default",
                UserName: "lab-broken",
                Server: "https://127.0.0.1:6443",
                Status: KubeContextStatus.ConfigurationError,
                StatusMessage: "The kubeconfig entry is invalid.")
        ]);

        Assert.Equal(0, response.QueryableContextCount);
        Assert.Equal(1, response.AuthenticationExpiredContextCount);
        Assert.Equal(1, response.ConfigurationErrorContextCount);
        Assert.Contains(response.Issues, issue => issue.Kind is AgentDiagnosticsIssueKind.KubeConfigLoadFailed);
        Assert.Contains(response.Issues, issue => issue.Kind is AgentDiagnosticsIssueKind.KubectlUnavailable);
        Assert.Contains(response.Issues, issue => issue.Kind is AgentDiagnosticsIssueKind.AuthenticationExpired);
        Assert.Contains(response.Issues, issue => issue.Kind is AgentDiagnosticsIssueKind.ConfigurationError);
    }

    [Fact]
    public void Create_AddsNoQueryableContextsIssueWhenEverythingIsBlocked()
    {
        var options = new AgentRuntimeOptions
        {
            PublicUrl = "http://127.0.0.1:46321",
            Origins = new OriginOptions
            {
                Interactive = ["http://localhost:5173"],
                PreviewPattern = "^https://preview.example$"
            }
        };

        var sessions = new PairingSessionRegistry(options);
        var probe = new Kuberkynesis.Agent.Kube.KubeBootstrapProbeResult(
            KubeConfigAvailable: true,
            KubectlAvailable: true,
            KubectlClientVersion: "v1.34.1",
            CurrentContextName: "lab-expired",
            ContextCount: 1,
            SourcePaths: [@"C:\temp\lab.kubeconfig"],
            Warnings: []);

        var response = new AgentDiagnosticsResponseFactory().Create(options, sessions, probe,
        [
            new KubeContextSummary(
                Name: "lab-expired",
                IsCurrent: true,
                ClusterName: "lab",
                Namespace: "default",
                UserName: "lab-expired",
                Server: "https://127.0.0.1:6443",
                Status: KubeContextStatus.AuthenticationExpired,
                StatusMessage: "Stored credentials were rejected.")
        ]);

        Assert.Contains(response.Issues, issue => issue.Kind is AgentDiagnosticsIssueKind.NoQueryableContexts);
    }
}
