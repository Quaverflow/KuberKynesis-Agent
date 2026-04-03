using k8s;
using k8s.Models;
using Kuberkynesis.Agent.Core.Configuration;
using Kuberkynesis.Ui.Shared.Kubernetes;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kuberkynesis.Agent.Kube;

public sealed class KubeResourceQueryService : IDisposable
{
    private const int DefaultLimit = 200;
    private const int MaxLimit = 500;
    private const int DefaultMaxParallelContextQueryCount = 6;
    private const int DefaultContextQueryTimeoutSeconds = 4;
    private static readonly TimeSpan KubectlFastPathTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ClientCacheSignatureTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan NamespaceQueryCacheDuration = TimeSpan.FromSeconds(20);
    private static readonly IReadOnlyList<KubeResourceKind> AllSupportedKinds =
    [
        KubeResourceKind.Namespace,
        KubeResourceKind.Node,
        KubeResourceKind.Pod,
        KubeResourceKind.Deployment,
        KubeResourceKind.ReplicaSet,
        KubeResourceKind.StatefulSet,
        KubeResourceKind.DaemonSet,
        KubeResourceKind.Service,
        KubeResourceKind.Ingress,
        KubeResourceKind.ConfigMap,
        KubeResourceKind.Secret,
        KubeResourceKind.Job,
        KubeResourceKind.CronJob,
        KubeResourceKind.Event
    ];

    private readonly IKubeConfigLoader kubeConfigLoader;
    private readonly TimeSpan contextQueryTimeout;
    private readonly ConcurrentDictionary<string, CachedKubernetesClient> clientCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CachedContextResourceQuery> namespaceQueryCache = new(StringComparer.Ordinal);

    public KubeResourceQueryService(IKubeConfigLoader kubeConfigLoader, AgentRuntimeOptions runtimeOptions)
    {
        this.kubeConfigLoader = kubeConfigLoader;
        contextQueryTimeout = TimeSpan.FromSeconds(
            Math.Max(1, runtimeOptions.ResourceQueries.ContextTimeoutSeconds <= 0
                ? DefaultContextQueryTimeoutSeconds
                : runtimeOptions.ResourceQueries.ContextTimeoutSeconds));
    }

    public async Task<KubeResourceQueryResponse> QueryAsync(KubeResourceQueryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var totalStopwatch = Stopwatch.StartNew();
        var loadStopwatch = Stopwatch.StartNew();
        var loadResult = kubeConfigLoader.Load();
        loadStopwatch.Stop();
        var normalizedLimit = NormalizeLimit(request.Limit);
        var warnings = loadResult.Warnings.Select(warning => new KubeQueryWarning(null, warning)).ToList();
        var contextTimings = new ConcurrentBag<KubeResourceContextQueryTiming>();

        if (loadResult.Contexts.Count is 0)
        {
            totalStopwatch.Stop();
            return new KubeResourceQueryResponse(
                request.Kind,
                Contexts: [],
                LimitApplied: normalizedLimit,
                Resources: [],
                Warnings: warnings,
                TransparencyCommands: [])
            {
                Performance = new KubeResourceQueryPerformance(
                    TotalMilliseconds: ToMilliseconds(totalStopwatch.Elapsed),
                    KubeConfigLoadMilliseconds: ToMilliseconds(loadStopwatch.Elapsed),
                    ContextResolutionMilliseconds: 0,
                    OrderingMilliseconds: 0,
                    Contexts: [])
            };
        }

        var contextResolutionStopwatch = Stopwatch.StartNew();
        var targetContexts = ResolveTargetContexts(request.Contexts, loadResult).ToArray();
        var targetKinds = ResolveTargetKinds(request).ToArray();
        contextResolutionStopwatch.Stop();
        var resources = new ConcurrentBag<KubeResourceSummary>();
        var contextWarnings = new ConcurrentQueue<KubeQueryWarning>();

        await Parallel.ForEachAsync(
            targetContexts,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Min(DefaultMaxParallelContextQueryCount, Math.Max(1, targetContexts.Length))
            },
            async (context, probeCancellationToken) =>
            {
                var transportKinds = new HashSet<string>(StringComparer.Ordinal);
                var clientAcquireMilliseconds = 0;
                var queryMilliseconds = 0;
                var filterMilliseconds = 0;
                var returnedResourceCount = 0;
                var matchedResourceCount = 0;

                if (context.Status is not KubeContextStatus.Configured)
                {
                    var error = context.StatusMessage ?? "The kube context is not currently queryable.";
                    contextWarnings.Enqueue(new KubeQueryWarning(context.Name, error));
                    contextTimings.Add(new KubeResourceContextQueryTiming(
                        ContextName: context.Name,
                        TransportKind: "unavailable",
                        ClientAcquireMilliseconds: clientAcquireMilliseconds,
                        QueryMilliseconds: queryMilliseconds,
                        FilterMilliseconds: filterMilliseconds,
                        ReturnedResourceCount: returnedResourceCount,
                        MatchedResourceCount: matchedResourceCount,
                        Error: error));
                    return;
                }

                try
                {
                    var clientAcquireStopwatch = Stopwatch.StartNew();
                    var client = await GetOrCreateCachedClientAsync(loadResult, context.Name, probeCancellationToken);
                    clientAcquireStopwatch.Stop();
                    clientAcquireMilliseconds = ToMilliseconds(clientAcquireStopwatch.Elapsed);
                    using var contextQueryCancellation = CancellationTokenSource.CreateLinkedTokenSource(probeCancellationToken);
                    contextQueryCancellation.CancelAfter(contextQueryTimeout);

                    var queryStopwatch = Stopwatch.StartNew();
                    var contextResourceBuffer = new List<KubeResourceSummary>();
                    foreach (var kind in targetKinds)
                    {
                        var kindRequest = request with
                        {
                            Kind = kind,
                            IncludeAllSupportedKinds = false,
                            CustomResourceType = kind is KubeResourceKind.CustomResource ? request.CustomResourceType : null
                        };
                        var contextResources = await QueryContextAsync(
                            client,
                            context.Name,
                            kindRequest,
                            normalizedLimit,
                            contextQueryCancellation.Token);
                        returnedResourceCount += contextResources.Resources.Count;
                        contextResourceBuffer.AddRange(contextResources.Resources);
                        transportKinds.Add(contextResources.TransportKind);
                    }
                    queryStopwatch.Stop();
                    queryMilliseconds = ToMilliseconds(queryStopwatch.Elapsed);

                    var filterStopwatch = Stopwatch.StartNew();
                    foreach (var resource in contextResourceBuffer.Where(summary => KubeResourceSummaryFactory.MatchesSearch(summary, request.Search)))
                    {
                        resources.Add(resource);
                        matchedResourceCount++;
                    }
                    filterStopwatch.Stop();
                    filterMilliseconds = ToMilliseconds(filterStopwatch.Elapsed);

                    contextTimings.Add(new KubeResourceContextQueryTiming(
                        ContextName: context.Name,
                        TransportKind: ResolveTransportKind(transportKinds),
                        ClientAcquireMilliseconds: clientAcquireMilliseconds,
                        QueryMilliseconds: queryMilliseconds,
                        FilterMilliseconds: filterMilliseconds,
                        ReturnedResourceCount: returnedResourceCount,
                        MatchedResourceCount: matchedResourceCount));
                }
                catch (OperationCanceledException) when (!probeCancellationToken.IsCancellationRequested)
                {
                    var error = $"Timed out after {(int)contextQueryTimeout.TotalSeconds}s while querying {request.Kind}.";
                    contextWarnings.Enqueue(new KubeQueryWarning(context.Name, error));
                    contextTimings.Add(new KubeResourceContextQueryTiming(
                        ContextName: context.Name,
                        TransportKind: ResolveTransportKind(transportKinds),
                        ClientAcquireMilliseconds: clientAcquireMilliseconds,
                        QueryMilliseconds: queryMilliseconds,
                        FilterMilliseconds: filterMilliseconds,
                        ReturnedResourceCount: returnedResourceCount,
                        MatchedResourceCount: matchedResourceCount,
                        TimedOut: true,
                        Error: error));
                }
                catch (Exception exception)
                {
                    contextWarnings.Enqueue(new KubeQueryWarning(context.Name, exception.Message));
                    contextTimings.Add(new KubeResourceContextQueryTiming(
                        ContextName: context.Name,
                        TransportKind: ResolveTransportKind(transportKinds),
                        ClientAcquireMilliseconds: clientAcquireMilliseconds,
                        QueryMilliseconds: queryMilliseconds,
                        FilterMilliseconds: filterMilliseconds,
                        ReturnedResourceCount: returnedResourceCount,
                        MatchedResourceCount: matchedResourceCount,
                        Error: exception.Message));
                }
            });

        warnings.AddRange(contextWarnings);

        var orderingStopwatch = Stopwatch.StartNew();
        var orderedResources = resources
            .OrderBy(resource => resource.ContextName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(resource => resource.Namespace ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(resource => resource.Kind.ToString(), StringComparer.OrdinalIgnoreCase)
            .ThenBy(resource => resource.Name, StringComparer.OrdinalIgnoreCase)
            .Take(normalizedLimit)
            .ToArray();
        orderingStopwatch.Stop();
        totalStopwatch.Stop();

        return new KubeResourceQueryResponse(
            request.Kind,
            Contexts: targetContexts.Select(context => context.Name).ToArray(),
            LimitApplied: normalizedLimit,
            Resources: orderedResources,
            Warnings: warnings,
            TransparencyCommands: KubectlTransparencyFactory.CreateForQuery(
                request,
                targetContexts.Select(context => context.Name).ToArray(),
                normalizedLimit))
        {
            Performance = new KubeResourceQueryPerformance(
                TotalMilliseconds: ToMilliseconds(totalStopwatch.Elapsed),
                KubeConfigLoadMilliseconds: ToMilliseconds(loadStopwatch.Elapsed),
                ContextResolutionMilliseconds: ToMilliseconds(contextResolutionStopwatch.Elapsed),
                OrderingMilliseconds: ToMilliseconds(orderingStopwatch.Elapsed),
                Contexts: contextTimings
                    .OrderBy(static timing => timing.ContextName, StringComparer.OrdinalIgnoreCase)
                    .ToArray())
        };
    }

    private static int ToMilliseconds(TimeSpan elapsed)
    {
        return (int)Math.Round(elapsed.TotalMilliseconds, MidpointRounding.AwayFromZero);
    }

    internal static IReadOnlyList<DiscoveredKubeContext> ResolveTargetContexts(
        IReadOnlyList<string> requestedContexts,
        KubeConfigLoadResult loadResult)
    {
        if (requestedContexts.Count is 0)
        {
            return loadResult.Contexts.Where(context => context.IsCurrent).Take(1).ToArray();
        }

        var contextMap = loadResult.Contexts.ToDictionary(context => context.Name, StringComparer.Ordinal);
        var resolved = new List<DiscoveredKubeContext>(requestedContexts.Count);
        var missingContexts = new List<string>();

        foreach (var requestedContext in requestedContexts.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (contextMap.TryGetValue(requestedContext, out var context))
            {
                resolved.Add(context);
            }
            else
            {
                missingContexts.Add(requestedContext);
            }
        }

        if (missingContexts.Count > 0)
        {
            throw new ArgumentException($"Unknown kube contexts: {string.Join(", ", missingContexts)}");
        }

        return resolved;
    }

    internal static IReadOnlyList<KubeResourceKind> ResolveTargetKinds(KubeResourceQueryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.IncludeAllSupportedKinds)
        {
            return [request.Kind];
        }

        return string.IsNullOrWhiteSpace(request.Namespace)
            ? AllSupportedKinds
            : AllSupportedKinds.Where(static kind => kind is not KubeResourceKind.Namespace and not KubeResourceKind.Node).ToArray();
    }

    internal static int NormalizeLimit(int requestedLimit)
    {
        if (requestedLimit <= 0)
        {
            return DefaultLimit;
        }

        return Math.Min(requestedLimit, MaxLimit);
    }

    private Task<ContextQueryExecutionResult> QueryContextAsync(
        Kubernetes client,
        string contextName,
        KubeResourceQueryRequest request,
        int limit,
        CancellationToken cancellationToken)
    {
        return request.Kind switch
        {
            KubeResourceKind.CustomResource => WrapQueryResultAsync(QueryCustomResourcesAsync(client, contextName, request, limit, cancellationToken)),
            KubeResourceKind.Namespace => QueryCachedNamespacesAsync(client, contextName, limit, cancellationToken),
            KubeResourceKind.Node => WrapQueryResultAsync(QueryNodesAsync(client, contextName, limit, cancellationToken)),
            KubeResourceKind.Pod => QueryPodsAsync(client, contextName, request.Namespace, limit, cancellationToken),
            KubeResourceKind.Deployment => WrapQueryResultAsync(QueryDeploymentsAsync(client, contextName, request.Namespace, limit, cancellationToken)),
            KubeResourceKind.ReplicaSet => WrapQueryResultAsync(QueryReplicaSetsAsync(client, contextName, request.Namespace, limit, cancellationToken)),
            KubeResourceKind.StatefulSet => WrapQueryResultAsync(QueryStatefulSetsAsync(client, contextName, request.Namespace, limit, cancellationToken)),
            KubeResourceKind.DaemonSet => WrapQueryResultAsync(QueryDaemonSetsAsync(client, contextName, request.Namespace, limit, cancellationToken)),
            KubeResourceKind.Service => WrapQueryResultAsync(QueryServicesAsync(client, contextName, request.Namespace, limit, cancellationToken)),
            KubeResourceKind.Ingress => WrapQueryResultAsync(QueryIngressesAsync(client, contextName, request.Namespace, limit, cancellationToken)),
            KubeResourceKind.ConfigMap => WrapQueryResultAsync(QueryConfigMapsAsync(client, contextName, request.Namespace, limit, cancellationToken)),
            KubeResourceKind.Secret => WrapQueryResultAsync(QuerySecretsAsync(client, contextName, request.Namespace, limit, cancellationToken)),
            KubeResourceKind.Job => WrapQueryResultAsync(QueryJobsAsync(client, contextName, request.Namespace, limit, cancellationToken)),
            KubeResourceKind.CronJob => WrapQueryResultAsync(QueryCronJobsAsync(client, contextName, request.Namespace, limit, cancellationToken)),
            KubeResourceKind.Event => WrapQueryResultAsync(QueryEventsAsync(client, contextName, request.Namespace, limit, cancellationToken)),
            _ => throw new ArgumentOutOfRangeException(nameof(request.Kind), request.Kind, "Unsupported Kubernetes resource kind.")
        };
    }

    private async Task<ContextQueryExecutionResult> QueryCachedNamespacesAsync(
        Kubernetes client,
        string contextName,
        int limit,
        CancellationToken cancellationToken)
    {
        var kubectlResources = await TryQueryNamespacesViaKubectlAsync(contextName, limit, cancellationToken);

        if (kubectlResources is not null)
        {
            return new ContextQueryExecutionResult(kubectlResources, "kubectl");
        }

        var cacheKey = $"{contextName}|{limit}";
        var resources = await QueryCachedContextResourcesAsync(
            namespaceQueryCache,
            cacheKey,
            () => QueryNamespacesAsync(client, contextName, limit, cancellationToken),
            NamespaceQueryCacheDuration,
            cancellationToken);
        return new ContextQueryExecutionResult(resources, "typed-client");
    }

    private static async Task<IReadOnlyList<KubeResourceSummary>> QueryCachedContextResourcesAsync(
        ConcurrentDictionary<string, CachedContextResourceQuery> cache,
        string cacheKey,
        Func<Task<IReadOnlyList<KubeResourceSummary>>> loader,
        TimeSpan cacheDuration,
        CancellationToken cancellationToken)
    {
        var entry = cache.GetOrAdd(cacheKey, static _ => new CachedContextResourceQuery());
        var now = DateTimeOffset.UtcNow;

        if (entry.Resources is not null && entry.ExpiresAtUtc > now)
        {
            return entry.Resources;
        }

        await entry.Gate.WaitAsync(cancellationToken);

        try
        {
            now = DateTimeOffset.UtcNow;

            if (entry.Resources is not null && entry.ExpiresAtUtc > now)
            {
                return entry.Resources;
            }

            var resources = await loader();
            entry.Resources = resources;
            entry.ExpiresAtUtc = now.Add(cacheDuration);
            return resources;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private static async Task<ContextQueryExecutionResult> WrapQueryResultAsync(Task<IReadOnlyList<KubeResourceSummary>> queryTask)
    {
        return new ContextQueryExecutionResult(await queryTask, "typed-client");
    }

    private static async Task<IReadOnlyList<KubeResourceSummary>?> TryQueryNamespacesViaKubectlAsync(
        string contextName,
        int limit,
        CancellationToken cancellationToken)
    {
        var list = await TryRunKubectlListAsync<V1NamespaceList>(
            contextName,
            namespaceName: null,
            resourceName: "namespaces",
            cancellationToken);

        return list?.Items
            .Select(item => KubeResourceSummaryFactory.Create(contextName, item))
            .Take(limit)
            .ToArray();
    }

    private static async Task<IReadOnlyList<KubeResourceSummary>?> TryQueryPodsViaKubectlAsync(
        string contextName,
        string? namespaceName,
        int limit,
        CancellationToken cancellationToken)
    {
        var list = await TryRunKubectlListAsync<V1PodList>(
            contextName,
            namespaceName,
            resourceName: "pods",
            cancellationToken);

        return list?.Items
            .Select(item => KubeResourceSummaryFactory.Create(contextName, item))
            .Take(limit)
            .ToArray();
    }

    private static async Task<TList?> TryRunKubectlListAsync<TList>(
        string contextName,
        string? namespaceName,
        string resourceName,
        CancellationToken cancellationToken)
        where TList : class
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "kubectl",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };

        process.StartInfo.ArgumentList.Add("--context");
        process.StartInfo.ArgumentList.Add(contextName);

        if (string.IsNullOrWhiteSpace(namespaceName))
        {
            if (string.Equals(resourceName, "pods", StringComparison.Ordinal))
            {
                process.StartInfo.ArgumentList.Add("-A");
            }
        }
        else
        {
            process.StartInfo.ArgumentList.Add("-n");
            process.StartInfo.ArgumentList.Add(namespaceName.Trim());
        }

        process.StartInfo.ArgumentList.Add("get");
        process.StartInfo.ArgumentList.Add(resourceName);
        process.StartInfo.ArgumentList.Add("-o");
        process.StartInfo.ArgumentList.Add("json");
        process.StartInfo.ArgumentList.Add("--request-timeout=15s");

        try
        {
            process.Start();
        }
        catch
        {
            return null;
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(KubectlFastPathTimeout);

        try
        {
            await process.WaitForExitAsync(timeoutCancellation.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryTerminate(process);
            return null;
        }

        var stdout = await stdoutTask;
        _ = await stderrTask;

        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<TList>(stdout);
        }
        catch
        {
            return null;
        }
    }

    private async Task<Kubernetes> GetOrCreateCachedClientAsync(
        KubeConfigLoadResult loadResult,
        string contextName,
        CancellationToken cancellationToken)
    {
        var cacheKey = CreateClientCacheKey(loadResult, contextName);
        var entry = clientCache.GetOrAdd(cacheKey, static _ => new CachedKubernetesClient());

        await entry.Gate.WaitAsync(cancellationToken);

        try
        {
            entry.LastAccessedUtc = DateTimeOffset.UtcNow;
            entry.Client ??= kubeConfigLoader.CreateClient(loadResult, contextName);
            return entry.Client;
        }
        finally
        {
            entry.Gate.Release();
            TrimStaleClientCache(cacheKey);
        }
    }

    private void TrimStaleClientCache(string activeCacheKey)
    {
        var cutoff = DateTimeOffset.UtcNow.Subtract(ClientCacheSignatureTtl);

        foreach (var pair in clientCache)
        {
            if (string.Equals(pair.Key, activeCacheKey, StringComparison.Ordinal) ||
                pair.Value.LastAccessedUtc >= cutoff)
            {
                continue;
            }

            if (!clientCache.TryRemove(pair.Key, out var removedEntry))
            {
                continue;
            }

            removedEntry.Client?.Dispose();
            removedEntry.Gate.Dispose();
        }
    }

    private static string CreateClientCacheKey(KubeConfigLoadResult loadResult, string contextName)
    {
        var sourceSignature = loadResult.SourcePaths.Count is 0
            ? "no-kubeconfig"
            : string.Join(
                "|",
                loadResult.SourcePaths.Select(static path =>
                {
                    path.Refresh();
                    return $"{path.FullName}:{path.Length}:{path.LastWriteTimeUtc.Ticks}";
                }));

        return $"{contextName}|{sourceSignature}";
    }

    private static async Task<IReadOnlyList<KubeResourceSummary>> QueryCustomResourcesAsync(
        Kubernetes client,
        string contextName,
        KubeResourceQueryRequest request,
        int limit,
        CancellationToken cancellationToken)
    {
        var customResourceType = request.CustomResourceType
            ?? throw new ArgumentException("A custom resource definition is required for custom resource queries.", nameof(request));
        var operations = (ICustomObjectsOperations)client;

        object response = customResourceType.Namespaced
            ? string.IsNullOrWhiteSpace(request.Namespace)
                ? await k8s.CustomObjectsOperationsExtensions.ListCustomObjectForAllNamespacesAsync(
                    operations,
                    customResourceType.Group,
                    customResourceType.Version,
                    customResourceType.Plural,
                    limit: limit,
                    cancellationToken: cancellationToken)
                : await k8s.CustomObjectsOperationsExtensions.ListNamespacedCustomObjectAsync(
                    operations,
                    customResourceType.Group,
                    customResourceType.Version,
                    request.Namespace.Trim(),
                    customResourceType.Plural,
                    limit: limit,
                    cancellationToken: cancellationToken)
            : await k8s.CustomObjectsOperationsExtensions.ListClusterCustomObjectAsync(
                operations,
                customResourceType.Group,
                customResourceType.Version,
                customResourceType.Plural,
                limit: limit,
                cancellationToken: cancellationToken);

        var root = JsonSerializer.SerializeToNode(response) as JsonObject;
        var items = root?["items"] as JsonArray;

        if (items is null)
        {
            return [];
        }

        return items
            .OfType<JsonObject>()
            .Select(item => KubeResourceSummaryFactory.Create(contextName, customResourceType, item))
            .ToArray();
    }

    private static async Task<IReadOnlyList<KubeResourceSummary>> QueryNamespacesAsync(
        Kubernetes client,
        string contextName,
        int limit,
        CancellationToken cancellationToken)
    {
        var list = await client.ListNamespaceAsync(limit: limit, cancellationToken: cancellationToken);
        return list.Items.Select(item => KubeResourceSummaryFactory.Create(contextName, item)).ToArray();
    }

    private sealed class CachedContextResourceQuery
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public DateTimeOffset ExpiresAtUtc { get; set; } = DateTimeOffset.MinValue;

        public IReadOnlyList<KubeResourceSummary>? Resources { get; set; }
    }

    private sealed class CachedKubernetesClient
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public Kubernetes? Client { get; set; }

        public DateTimeOffset LastAccessedUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    private static async Task<IReadOnlyList<KubeResourceSummary>> QueryNodesAsync(
        Kubernetes client,
        string contextName,
        int limit,
        CancellationToken cancellationToken)
    {
        var list = await client.ListNodeAsync(limit: limit, cancellationToken: cancellationToken);
        return list.Items.Select(item => KubeResourceSummaryFactory.Create(contextName, item)).ToArray();
    }

    private static async Task<ContextQueryExecutionResult> QueryPodsAsync(
        Kubernetes client,
        string contextName,
        string? namespaceName,
        int limit,
        CancellationToken cancellationToken)
    {
        var kubectlResources = await TryQueryPodsViaKubectlAsync(contextName, namespaceName, limit, cancellationToken);

        if (kubectlResources is not null)
        {
            return new ContextQueryExecutionResult(kubectlResources, "kubectl");
        }

        var list = string.IsNullOrWhiteSpace(namespaceName)
            ? await client.ListPodForAllNamespacesAsync(limit: limit, cancellationToken: cancellationToken)
            : await client.ListNamespacedPodAsync(namespaceName, limit: limit, cancellationToken: cancellationToken);

        return new ContextQueryExecutionResult(
            list.Items.Select(item => KubeResourceSummaryFactory.Create(contextName, item)).ToArray(),
            "typed-client");
    }

    private static async Task<IReadOnlyList<KubeResourceSummary>> QueryDeploymentsAsync(
        Kubernetes client,
        string contextName,
        string? namespaceName,
        int limit,
        CancellationToken cancellationToken)
    {
        var list = string.IsNullOrWhiteSpace(namespaceName)
            ? await client.ListDeploymentForAllNamespacesAsync(limit: limit, cancellationToken: cancellationToken)
            : await client.ListNamespacedDeploymentAsync(namespaceName, limit: limit, cancellationToken: cancellationToken);

        return list.Items.Select(item => KubeResourceSummaryFactory.Create(contextName, item)).ToArray();
    }

    private static async Task<IReadOnlyList<KubeResourceSummary>> QueryReplicaSetsAsync(
        Kubernetes client,
        string contextName,
        string? namespaceName,
        int limit,
        CancellationToken cancellationToken)
    {
        var list = string.IsNullOrWhiteSpace(namespaceName)
            ? await client.ListReplicaSetForAllNamespacesAsync(limit: limit, cancellationToken: cancellationToken)
            : await client.ListNamespacedReplicaSetAsync(namespaceName, limit: limit, cancellationToken: cancellationToken);

        return list.Items.Select(item => KubeResourceSummaryFactory.Create(contextName, item)).ToArray();
    }

    private static async Task<IReadOnlyList<KubeResourceSummary>> QueryStatefulSetsAsync(
        Kubernetes client,
        string contextName,
        string? namespaceName,
        int limit,
        CancellationToken cancellationToken)
    {
        var list = string.IsNullOrWhiteSpace(namespaceName)
            ? await client.ListStatefulSetForAllNamespacesAsync(limit: limit, cancellationToken: cancellationToken)
            : await client.ListNamespacedStatefulSetAsync(namespaceName, limit: limit, cancellationToken: cancellationToken);

        return list.Items.Select(item => KubeResourceSummaryFactory.Create(contextName, item)).ToArray();
    }

    private static async Task<IReadOnlyList<KubeResourceSummary>> QueryDaemonSetsAsync(
        Kubernetes client,
        string contextName,
        string? namespaceName,
        int limit,
        CancellationToken cancellationToken)
    {
        var list = string.IsNullOrWhiteSpace(namespaceName)
            ? await client.ListDaemonSetForAllNamespacesAsync(limit: limit, cancellationToken: cancellationToken)
            : await client.ListNamespacedDaemonSetAsync(namespaceName, limit: limit, cancellationToken: cancellationToken);

        return list.Items.Select(item => KubeResourceSummaryFactory.Create(contextName, item)).ToArray();
    }

    private static async Task<IReadOnlyList<KubeResourceSummary>> QueryServicesAsync(
        Kubernetes client,
        string contextName,
        string? namespaceName,
        int limit,
        CancellationToken cancellationToken)
    {
        var list = string.IsNullOrWhiteSpace(namespaceName)
            ? await client.ListServiceForAllNamespacesAsync(limit: limit, cancellationToken: cancellationToken)
            : await client.ListNamespacedServiceAsync(namespaceName, limit: limit, cancellationToken: cancellationToken);

        return list.Items.Select(item => KubeResourceSummaryFactory.Create(contextName, item)).ToArray();
    }

    private static async Task<IReadOnlyList<KubeResourceSummary>> QueryIngressesAsync(
        Kubernetes client,
        string contextName,
        string? namespaceName,
        int limit,
        CancellationToken cancellationToken)
    {
        var list = string.IsNullOrWhiteSpace(namespaceName)
            ? await client.ListIngressForAllNamespacesAsync(limit: limit, cancellationToken: cancellationToken)
            : await client.ListNamespacedIngressAsync(namespaceName, limit: limit, cancellationToken: cancellationToken);

        return list.Items.Select(item => KubeResourceSummaryFactory.Create(contextName, item)).ToArray();
    }

    private static async Task<IReadOnlyList<KubeResourceSummary>> QueryConfigMapsAsync(
        Kubernetes client,
        string contextName,
        string? namespaceName,
        int limit,
        CancellationToken cancellationToken)
    {
        var list = string.IsNullOrWhiteSpace(namespaceName)
            ? await client.ListConfigMapForAllNamespacesAsync(limit: limit, cancellationToken: cancellationToken)
            : await client.ListNamespacedConfigMapAsync(namespaceName, limit: limit, cancellationToken: cancellationToken);

        return list.Items.Select(item => KubeResourceSummaryFactory.Create(contextName, item)).ToArray();
    }

    private static async Task<IReadOnlyList<KubeResourceSummary>> QuerySecretsAsync(
        Kubernetes client,
        string contextName,
        string? namespaceName,
        int limit,
        CancellationToken cancellationToken)
    {
        var list = string.IsNullOrWhiteSpace(namespaceName)
            ? await client.ListSecretForAllNamespacesAsync(limit: limit, cancellationToken: cancellationToken)
            : await client.ListNamespacedSecretAsync(namespaceName, limit: limit, cancellationToken: cancellationToken);

        return list.Items.Select(item => KubeResourceSummaryFactory.Create(contextName, item)).ToArray();
    }

    private static async Task<IReadOnlyList<KubeResourceSummary>> QueryJobsAsync(
        Kubernetes client,
        string contextName,
        string? namespaceName,
        int limit,
        CancellationToken cancellationToken)
    {
        var list = string.IsNullOrWhiteSpace(namespaceName)
            ? await client.ListJobForAllNamespacesAsync(limit: limit, cancellationToken: cancellationToken)
            : await client.ListNamespacedJobAsync(namespaceName, limit: limit, cancellationToken: cancellationToken);

        return list.Items.Select(item => KubeResourceSummaryFactory.Create(contextName, item)).ToArray();
    }

    private static async Task<IReadOnlyList<KubeResourceSummary>> QueryCronJobsAsync(
        Kubernetes client,
        string contextName,
        string? namespaceName,
        int limit,
        CancellationToken cancellationToken)
    {
        var list = string.IsNullOrWhiteSpace(namespaceName)
            ? await client.ListCronJobForAllNamespacesAsync(limit: limit, cancellationToken: cancellationToken)
            : await client.ListNamespacedCronJobAsync(namespaceName, limit: limit, cancellationToken: cancellationToken);

        return list.Items.Select(item => KubeResourceSummaryFactory.Create(contextName, item)).ToArray();
    }

    private static async Task<IReadOnlyList<KubeResourceSummary>> QueryEventsAsync(
        Kubernetes client,
        string contextName,
        string? namespaceName,
        int limit,
        CancellationToken cancellationToken)
    {
        var coreOperations = (ICoreV1Operations)client;
        var list = string.IsNullOrWhiteSpace(namespaceName)
            ? await k8s.CoreV1OperationsExtensions.ListEventForAllNamespacesAsync(
                coreOperations,
                limit: limit,
                cancellationToken: cancellationToken)
            : await k8s.CoreV1OperationsExtensions.ListNamespacedEventAsync(
                coreOperations,
                namespaceName,
                limit: limit,
                cancellationToken: cancellationToken);

        return list.Items.Select(item => KubeResourceSummaryFactory.Create(contextName, item)).ToArray();
    }

    private static string ResolveTransportKind(IReadOnlyCollection<string> transportKinds)
    {
        return transportKinds.Count switch
        {
            0 => "typed-client",
            1 => transportKinds.First(),
            _ => "mixed"
        };
    }

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Ignore best-effort cleanup failures.
        }
    }

    private sealed record ContextQueryExecutionResult(
        IReadOnlyList<KubeResourceSummary> Resources,
        string TransportKind);

    public void Dispose()
    {
        foreach (var entry in clientCache.Values)
        {
            entry.Client?.Dispose();
            entry.Gate.Dispose();
        }

        foreach (var entry in namespaceQueryCache.Values)
        {
            entry.Gate.Dispose();
        }
    }
}
