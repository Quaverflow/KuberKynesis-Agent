using Kuberkynesis.Agent.Kube;
using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Tests;

public sealed class KubeWorkspaceResolveServiceTests
{
    [Fact]
    public async Task ResolveAsync_DropsMissingAndUnavailableRequestedContexts()
    {
        var service = CreateService(
            currentContextName: "kind-kuberkynesis-lab",
            [
                CreateContext("kind-kuberkynesis-lab", isCurrent: true, KubeContextStatus.Configured),
                CreateContext("stale-prod", isCurrent: false, KubeContextStatus.AuthenticationExpired, "Cluster credentials expired.")
            ]);

        var response = await service.ResolveAsync(
            new KubeWorkspaceResolveRequest
            {
                Kind = KubeResourceKind.Pod,
                Contexts = ["kind-kuberkynesis-lab", "stale-prod", "missing-lab"],
                Namespace = "orders-prod",
                Search = " checkout ",
                Limit = 0
            },
            CancellationToken.None);

        Assert.Equal(["kind-kuberkynesis-lab"], response.ResolvedQuery.Contexts);
        Assert.Equal("orders-prod", response.ResolvedQuery.Namespace);
        Assert.Equal("checkout", response.ResolvedQuery.Search);
        Assert.Equal(200, response.ResolvedQuery.Limit);
        Assert.Equal([KubeResourceKind.Pod], response.ResolvedKinds);
        Assert.Equal(["missing-lab"], response.MissingContexts);
        Assert.Equal("stale-prod", Assert.Single(response.UnavailableContexts).Name);
        Assert.False(response.UsedCurrentContextFallback);
        Assert.False(response.IgnoredNamespaceFilter);
        Assert.Contains("Requested workspace context 'missing-lab' is no longer present in kubeconfig.", response.Warnings);
        Assert.Contains(response.Warnings, warning => warning.Contains("stale-prod", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResolveAsync_FallsBackToTheCurrentQueryableContextAndClearsClusterScopedNamespaceFilters()
    {
        var service = CreateService(
            currentContextName: "kind-kuberkynesis-lab",
            [
                CreateContext("kind-kuberkynesis-lab", isCurrent: true, KubeContextStatus.Configured),
                CreateContext("broken-dev", isCurrent: false, KubeContextStatus.ConfigurationError, "The referenced cluster entry is missing from kubeconfig.")
            ]);

        var response = await service.ResolveAsync(
            new KubeWorkspaceResolveRequest
            {
                Kind = KubeResourceKind.Node,
                Contexts = ["broken-dev"],
                Namespace = "orders-prod"
            },
            CancellationToken.None);

        Assert.Equal(["kind-kuberkynesis-lab"], response.ResolvedQuery.Contexts);
        Assert.Null(response.ResolvedQuery.Namespace);
        Assert.True(response.UsedCurrentContextFallback);
        Assert.True(response.IgnoredNamespaceFilter);
        Assert.Equal("kind-kuberkynesis-lab", Assert.Single(response.ResolvedContexts).Name);
        Assert.Equal("broken-dev", Assert.Single(response.UnavailableContexts).Name);
        Assert.Contains(response.Warnings, warning => warning.Contains("Fell back to 'kind-kuberkynesis-lab'", StringComparison.Ordinal));
        Assert.Contains(response.Warnings, warning => warning.Contains("namespace filter was cleared", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("cluster scope", response.ScopeSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("kind-kuberkynesis-lab", Assert.Single(response.ResolvedClusters).Name);
    }

    [Fact]
    public async Task ResolveAsync_ReportsResolvedClusterSpanForMultiContextWorkspaces()
    {
        var service = CreateService(
            currentContextName: "kind-kuberkynesis-lab",
            [
                CreateContext("kind-kuberkynesis-lab", isCurrent: true, KubeContextStatus.Configured, clusterName: "kind-kuberkynesis-lab", server: "https://127.0.0.1:42317"),
                CreateContext("kind-kuberkynesis-stage", isCurrent: false, KubeContextStatus.Configured, clusterName: "kind-kuberkynesis-stage", server: "https://127.0.0.1:42318")
            ]);

        var response = await service.ResolveAsync(
            new KubeWorkspaceResolveRequest
            {
                Kind = KubeResourceKind.Pod,
                Contexts = ["kind-kuberkynesis-lab", "kind-kuberkynesis-stage"],
                Namespace = "checkout-prod"
            },
            CancellationToken.None);

        Assert.Equal(2, response.ResolvedClusters.Count);
        Assert.Equal("kind-kuberkynesis-lab", response.ResolvedClusters[0].Name);
        Assert.Equal("kind-kuberkynesis-stage", response.ResolvedClusters[1].Name);
        Assert.Contains("2 contexts", response.ScopeSummary, StringComparison.Ordinal);
        Assert.Contains("2 clusters", response.ScopeSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_ClearsNamespaceFiltersForClusterScopedCustomResources()
    {
        var service = CreateService(
            currentContextName: "kind-kuberkynesis-lab",
            [
                CreateContext("kind-kuberkynesis-lab", isCurrent: true, KubeContextStatus.Configured)
            ]);

        var response = await service.ResolveAsync(
            new KubeWorkspaceResolveRequest
            {
                Kind = KubeResourceKind.CustomResource,
                CustomResourceType = new KubeCustomResourceType(
                    Group: "stable.example.io",
                    Version: "v1",
                    Kind: "ClusterWidget",
                    Plural: "clusterwidgets",
                    Namespaced: false),
                Namespace = "orders-prod"
            },
            CancellationToken.None);

        Assert.Null(response.ResolvedQuery.Namespace);
        Assert.True(response.IgnoredNamespaceFilter);
        Assert.Contains(response.Warnings, warning => warning.Contains("ClusterWidget", StringComparison.Ordinal));
    }

    private static KubeWorkspaceResolveService CreateService(string? currentContextName, IReadOnlyList<DiscoveredKubeContext> contexts)
    {
        var loader = new StubKubeConfigLoader(
            new KubeConfigLoadResult(
                Configuration: null,
                SourcePaths: [],
                CurrentContextName: currentContextName,
                Contexts: contexts,
                Warnings: []));

        return new KubeWorkspaceResolveService(new KubeContextDiscoveryService(loader));
    }

    private static DiscoveredKubeContext CreateContext(
        string name,
        bool isCurrent,
        KubeContextStatus status,
        string? statusMessage = null,
        string? clusterName = null,
        string? server = null)
    {
        return new DiscoveredKubeContext(
            Name: name,
            IsCurrent: isCurrent,
            ClusterName: clusterName ?? name,
            Namespace: "default",
            UserName: "developer",
            Server: server ?? "https://example.invalid",
            Status: status,
            StatusMessage: statusMessage);
    }

    private sealed class StubKubeConfigLoader(KubeConfigLoadResult loadResult) : IKubeConfigLoader
    {
        public KubeConfigLoadResult Load() => loadResult;

        public k8s.Kubernetes CreateClient(KubeConfigLoadResult ignoredLoadResult, string contextName)
        {
            throw new NotSupportedException("Workspace resolve tests do not create live Kubernetes clients.");
        }
    }
}
