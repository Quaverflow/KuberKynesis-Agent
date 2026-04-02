using System.Net;
using Kuberkynesis.Agent.Kube;
using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Tests;

public sealed class KubeContextProbeClassifierTests
{
    [Fact]
    public void ClassifyProbeFailure_ReturnsAuthenticationExpiredForUnauthorizedResponses()
    {
        var result = KubeContextProbeClassifier.ClassifyProbeFailure(
            "dev-eu",
            HttpStatusCode.Unauthorized,
            "Unauthorized");

        Assert.Equal(KubeContextStatus.AuthenticationExpired, result.Status);
        Assert.Contains("rejected the stored credentials", result.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClassifyProbeFailure_KeepsContextConfiguredForForbiddenProbeResults()
    {
        var result = KubeContextProbeClassifier.ClassifyProbeFailure(
            "dev-eu",
            HttpStatusCode.Forbidden,
            "Forbidden");

        Assert.Equal(KubeContextStatus.Configured, result.Status);
        Assert.Contains("forbidden by RBAC", result.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClassifyProbeFailure_ReturnsConfigurationErrorForConnectivityProblems()
    {
        var result = KubeContextProbeClassifier.ClassifyProbeFailure(
            "dev-eu",
            statusCode: null,
            message: "dial tcp 127.0.0.1:6443: connectex: No connection could be made because the target machine actively refused it.");

        Assert.Equal(KubeContextStatus.ConfigurationError, result.Status);
        Assert.Contains("could not reach the cluster", result.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }
}
