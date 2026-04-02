using k8s;
using k8s.Autorest;
using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Kube;

public sealed class KubeContextDiscoveryService
{
    private const int DefaultMaxParallelProbeCount = 8;
    private static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultCacheLifetime = TimeSpan.FromSeconds(30);

    private readonly IKubeConfigLoader kubeConfigLoader;
    private readonly Func<KubeConfigLoadResult, DiscoveredKubeContext, CancellationToken, Task<DiscoveredKubeContext>> probeContextAsync;
    private readonly int maxParallelProbeCount;
    private readonly TimeSpan cacheLifetime;
    private readonly Lock cacheGate = new();
    private CachedContextsSnapshot? cachedContexts;
    private Task<KubeContextsResponse>? inFlightContextsTask;

    public KubeContextDiscoveryService(IKubeConfigLoader kubeConfigLoader)
        : this(
            kubeConfigLoader,
            probeContextAsync: null,
            maxParallelProbeCount: DefaultMaxParallelProbeCount,
            cacheLifetime: DefaultCacheLifetime)
    {
    }

    internal KubeContextDiscoveryService(
        IKubeConfigLoader kubeConfigLoader,
        Func<KubeConfigLoadResult, DiscoveredKubeContext, CancellationToken, Task<DiscoveredKubeContext>>? probeContextAsync,
        int maxParallelProbeCount,
        TimeSpan cacheLifetime)
    {
        this.kubeConfigLoader = kubeConfigLoader;
        this.probeContextAsync = probeContextAsync ?? ProbeConfiguredContextAsync;
        this.maxParallelProbeCount = Math.Max(1, maxParallelProbeCount);
        this.cacheLifetime = cacheLifetime <= TimeSpan.Zero ? DefaultCacheLifetime : cacheLifetime;
    }

    public async Task<KubeContextsResponse> GetContextsAsync(CancellationToken cancellationToken = default)
    {
        Task<KubeContextsResponse> contextsTask;

        lock (cacheGate)
        {
            if (cachedContexts is not null && cachedContexts.ExpiresAtUtc > DateTimeOffset.UtcNow)
            {
                return cachedContexts.Response;
            }

            inFlightContextsTask ??= LoadAndCacheContextsAsync();
            contextsTask = inFlightContextsTask;
        }

        return await contextsTask.WaitAsync(cancellationToken);
    }

    private async Task<KubeContextsResponse> LoadAndCacheContextsAsync()
    {
        try
        {
            var contextsResponse = await LoadContextsAsync(CancellationToken.None);

            lock (cacheGate)
            {
                cachedContexts = new CachedContextsSnapshot(
                    contextsResponse,
                    DateTimeOffset.UtcNow.Add(cacheLifetime));
            }

            return contextsResponse;
        }
        finally
        {
            lock (cacheGate)
            {
                inFlightContextsTask = null;
            }
        }
    }

    private async Task<KubeContextsResponse> LoadContextsAsync(CancellationToken cancellationToken)
    {
        var loadResult = kubeConfigLoader.Load();
        var contexts = await ProbeContextsAsync(loadResult, cancellationToken);
        var contextSummaries = contexts
            .Select(static context => new KubeContextSummary(
                context.Name,
                context.IsCurrent,
                context.ClusterName,
                context.Namespace,
                context.UserName,
                context.Server,
                context.Status,
                context.StatusMessage))
            .ToArray();

        return new KubeContextsResponse(
            CurrentContextName: loadResult.CurrentContextName,
            SourcePaths: loadResult.SourcePaths.Select(path => path.FullName).ToArray(),
            Contexts: contextSummaries,
            Warnings: loadResult.Warnings)
        {
            Clusters = KubeClusterSummaryFactory.Build(contextSummaries)
        };
    }

    private async Task<IReadOnlyList<DiscoveredKubeContext>> ProbeContextsAsync(
        KubeConfigLoadResult loadResult,
        CancellationToken cancellationToken)
    {
        if (loadResult.Configuration is null || loadResult.Contexts.Count is 0)
        {
            return loadResult.Contexts;
        }

        var probedContexts = new DiscoveredKubeContext[loadResult.Contexts.Count];
        var probeableContexts = new List<(int Index, DiscoveredKubeContext Context)>(loadResult.Contexts.Count);

        for (var index = 0; index < loadResult.Contexts.Count; index++)
        {
            var context = loadResult.Contexts[index];

            if (context.Status is not KubeContextStatus.Configured)
            {
                probedContexts[index] = context;
                continue;
            }

            probeableContexts.Add((index, context));
        }

        await Parallel.ForEachAsync(
            probeableContexts,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = maxParallelProbeCount
            },
            async (entry, probeCancellationToken) =>
            {
                probedContexts[entry.Index] = await probeContextAsync(loadResult, entry.Context, probeCancellationToken);
            });

        return probedContexts;
    }

    private async Task<DiscoveredKubeContext> ProbeConfiguredContextAsync(
        KubeConfigLoadResult loadResult,
        DiscoveredKubeContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = kubeConfigLoader.CreateClient(loadResult, context.Name);
            using var probeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            probeCancellation.CancelAfter(DefaultProbeTimeout);

            await client.ListNamespaceAsync(limit: 1, cancellationToken: probeCancellation.Token);
            return context with { StatusMessage = null };
        }
        catch (HttpOperationException exception)
        {
            var classified = KubeContextProbeClassifier.ClassifyProbeFailure(
                context.Name,
                exception.Response.StatusCode,
                exception.Message);

            return context with
            {
                Status = classified.Status,
                StatusMessage = classified.StatusMessage
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var classified = KubeContextProbeClassifier.ClassifyProbeFailure(
                context.Name,
                statusCode: null,
                message: "The startup probe timed out.");

            return context with
            {
                Status = classified.Status,
                StatusMessage = classified.StatusMessage
            };
        }
        catch (Exception exception)
        {
            var classified = KubeContextProbeClassifier.ClassifyProbeFailure(
                context.Name,
                statusCode: null,
                message: exception.Message);

            return context with
            {
                Status = classified.Status,
                StatusMessage = classified.StatusMessage
            };
        }
    }

    private sealed record CachedContextsSnapshot(
        KubeContextsResponse Response,
        DateTimeOffset ExpiresAtUtc);
}
