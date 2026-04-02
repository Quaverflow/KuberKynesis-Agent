using System.Threading;
using k8s.KubeConfigModels;
using Kuberkynesis.Agent.Kube;
using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Tests;

public sealed class KubeContextDiscoveryServiceTests
{
    [Fact]
    public async Task GetContextsAsync_ProbesConfiguredContextsInParallel()
    {
        var loadResult = CreateLoadResult(
            CreateContext("alpha", isCurrent: true),
            CreateContext("beta"),
            CreateContext("gamma"),
            CreateContext("delta"));
        var loader = new StubKubeConfigLoader(loadResult);
        var readyToRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProbes = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedProbeCount = 0;
        var currentConcurrency = 0;
        var maxConcurrency = 0;
        var service = new KubeContextDiscoveryService(
            loader,
            async (_, context, cancellationToken) =>
            {
                var concurrency = Interlocked.Increment(ref currentConcurrency);
                UpdateMaxConcurrency(ref maxConcurrency, concurrency);

                if (Interlocked.Increment(ref startedProbeCount) >= 3)
                {
                    readyToRelease.TrySetResult(true);
                }

                await releaseProbes.Task.WaitAsync(cancellationToken);
                Interlocked.Decrement(ref currentConcurrency);
                return context with { StatusMessage = null };
            },
            maxParallelProbeCount: 3,
            cacheLifetime: TimeSpan.FromMinutes(1));

        var responseTask = service.GetContextsAsync();
        await readyToRelease.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(Volatile.Read(ref maxConcurrency) >= 3);

        releaseProbes.TrySetResult(true);
        var response = await responseTask;

        Assert.Equal(4, response.Contexts.Count);
        Assert.All(response.Contexts, context => Assert.Equal(KubeContextStatus.Configured, context.Status));
    }

    [Fact]
    public async Task GetContextsAsync_ReusesRecentCachedResults()
    {
        var loadResult = CreateLoadResult(
            CreateContext("alpha", isCurrent: true),
            CreateContext("beta"));
        var loader = new StubKubeConfigLoader(loadResult);
        var probeCount = 0;
        var service = new KubeContextDiscoveryService(
            loader,
            (_, context, _) =>
            {
                Interlocked.Increment(ref probeCount);
                return Task.FromResult(context with { StatusMessage = null });
            },
            maxParallelProbeCount: 2,
            cacheLifetime: TimeSpan.FromMinutes(1));

        var firstResponse = await service.GetContextsAsync();
        var secondResponse = await service.GetContextsAsync();

        Assert.Equal(2, probeCount);
        Assert.Same(firstResponse, secondResponse);
    }

    private static KubeConfigLoadResult CreateLoadResult(params DiscoveredKubeContext[] contexts)
    {
        return new KubeConfigLoadResult(
            Configuration: new K8SConfiguration(),
            SourcePaths: [],
            CurrentContextName: contexts.FirstOrDefault(static context => context.IsCurrent)?.Name,
            Contexts: contexts,
            Warnings: []);
    }

    private static DiscoveredKubeContext CreateContext(string name, bool isCurrent = false)
    {
        return new DiscoveredKubeContext(
            Name: name,
            IsCurrent: isCurrent,
            ClusterName: $"{name}-cluster",
            Namespace: "default",
            UserName: $"{name}-user",
            Server: $"https://{name}.example.invalid",
            Status: KubeContextStatus.Configured,
            StatusMessage: null);
    }

    private static void UpdateMaxConcurrency(ref int maxConcurrency, int candidate)
    {
        while (true)
        {
            var observed = Volatile.Read(ref maxConcurrency);

            if (candidate <= observed)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref maxConcurrency, candidate, observed) == observed)
            {
                return;
            }
        }
    }

    private sealed class StubKubeConfigLoader(KubeConfigLoadResult loadResult) : IKubeConfigLoader
    {
        public KubeConfigLoadResult Load() => loadResult;

        public k8s.Kubernetes CreateClient(KubeConfigLoadResult ignoredLoadResult, string contextName)
        {
            throw new NotSupportedException("Discovery tests use the injected probe delegate instead of live Kubernetes clients.");
        }
    }
}
