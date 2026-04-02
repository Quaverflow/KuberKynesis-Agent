using Kuberkynesis.Agent.Kube;
using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Tests;

public sealed class KubeResourceQueryServiceTests
{
    [Fact]
    public void ResolveTargetContexts_UsesCurrentContextWhenRequestIsEmpty()
    {
        var loadResult = new KubeConfigLoadResult(
            Configuration: null,
            SourcePaths: [],
            CurrentContextName: "dev-eu",
            Contexts:
            [
                new DiscoveredKubeContext("dev-us", false, "dev-us", "apps", "developer", "https://dev-us.example", KubeContextStatus.Configured, null),
                new DiscoveredKubeContext("dev-eu", true, "dev-eu", "apps", "developer", "https://dev-eu.example", KubeContextStatus.Configured, null)
            ],
            Warnings: []);

        var resolved = KubeResourceQueryService.ResolveTargetContexts([], loadResult);

        var context = Assert.Single(resolved);
        Assert.Equal("dev-eu", context.Name);
    }

    [Fact]
    public void ResolveTargetContexts_ThrowsForUnknownContext()
    {
        var loadResult = new KubeConfigLoadResult(
            Configuration: null,
            SourcePaths: [],
            CurrentContextName: "dev-eu",
            Contexts:
            [
                new DiscoveredKubeContext("dev-eu", true, "dev-eu", "apps", "developer", "https://dev-eu.example", KubeContextStatus.Configured, null)
            ],
            Warnings: []);

        var exception = Assert.Throws<ArgumentException>(() =>
            KubeResourceQueryService.ResolveTargetContexts(["prod"], loadResult));

        Assert.Contains("prod", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveTargetKinds_ExpandsAllSupportedKindsWhenRequested()
    {
        var kinds = KubeResourceQueryService.ResolveTargetKinds(
            new KubeResourceQueryRequest
            {
                Kind = KubeResourceKind.Pod,
                IncludeAllSupportedKinds = true
            });

        Assert.Contains(KubeResourceKind.Pod, kinds);
        Assert.Contains(KubeResourceKind.Job, kinds);
        Assert.Contains(KubeResourceKind.Node, kinds);
        Assert.Contains(KubeResourceKind.Namespace, kinds);
    }

    [Fact]
    public void ResolveTargetKinds_OmitsClusterScopedKindsWhenNamespaceIsRestricted()
    {
        var kinds = KubeResourceQueryService.ResolveTargetKinds(
            new KubeResourceQueryRequest
            {
                Kind = KubeResourceKind.Pod,
                IncludeAllSupportedKinds = true,
                Namespace = "orders-prod"
            });

        Assert.DoesNotContain(KubeResourceKind.Node, kinds);
        Assert.DoesNotContain(KubeResourceKind.Namespace, kinds);
        Assert.Contains(KubeResourceKind.Pod, kinds);
        Assert.Contains(KubeResourceKind.CronJob, kinds);
    }
}
