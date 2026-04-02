using k8s;
using k8s.Autorest;
using k8s.Models;
using Kuberkynesis.Agent.Core.Configuration;
using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Kube;

public sealed class KubeResourceMetricsService
{
    private readonly IKubeConfigLoader kubeConfigLoader;
    private readonly PrometheusMetricsSource prometheusMetricsSource;
    private readonly AgentRuntimeOptions runtimeOptions;

    public KubeResourceMetricsService(
        IKubeConfigLoader kubeConfigLoader,
        PrometheusMetricsSource prometheusMetricsSource,
        AgentRuntimeOptions runtimeOptions)
    {
        this.kubeConfigLoader = kubeConfigLoader;
        this.prometheusMetricsSource = prometheusMetricsSource;
        this.runtimeOptions = runtimeOptions;
    }

    public async Task<KubeResourceMetricsResponse> GetResourceMetricsAsync(KubeResourceMetricsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.ContextName))
        {
            throw new ArgumentException("A kube context name is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ArgumentException("A resource name is required.", nameof(request));
        }

        var loadResult = kubeConfigLoader.Load();

        if (loadResult.Contexts.Count is 0)
        {
            throw new ArgumentException("No kube contexts were found.");
        }

        var context = KubeResourceQueryService.ResolveTargetContexts([request.ContextName], loadResult).Single();

        if (context.Status is KubeContextStatus.ConfigurationError)
        {
            throw new ArgumentException(context.StatusMessage ?? $"The kube context '{context.Name}' is invalid.");
        }

        using var client = kubeConfigLoader.CreateClient(loadResult, context.Name);

        return request.Kind switch
        {
            KubeResourceKind.Pod => await GetPodMetricsAsync(client, context.Name, RequireNamespace(request), request.Name.Trim(), cancellationToken),
            KubeResourceKind.Deployment => await GetDeploymentMetricsAsync(client, context.Name, RequireNamespace(request), request.Name.Trim(), cancellationToken),
            KubeResourceKind.ReplicaSet => await GetReplicaSetMetricsAsync(client, context.Name, RequireNamespace(request), request.Name.Trim(), cancellationToken),
            KubeResourceKind.StatefulSet => await GetStatefulSetMetricsAsync(client, context.Name, RequireNamespace(request), request.Name.Trim(), cancellationToken),
            KubeResourceKind.DaemonSet => await GetDaemonSetMetricsAsync(client, context.Name, RequireNamespace(request), request.Name.Trim(), cancellationToken),
            KubeResourceKind.Service => await GetServiceMetricsAsync(client, context.Name, RequireNamespace(request), request.Name.Trim(), cancellationToken),
            KubeResourceKind.Node => await GetNodeMetricsAsync(client, context.Name, request.Name.Trim(), cancellationToken),
            _ => CreateUnsupportedKindResponse(request, context.Name)
        };
    }

    public async Task<KubePodMetricsQueryResponse> QueryPodMetricsAsync(KubePodMetricsQueryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Targets.Count is 0)
        {
            return new KubePodMetricsQueryResponse(
                MetricsAvailable: false,
                MetricsSource: KubeMetricsSourceKind.Unavailable,
                MetricsStatusMessage: "No pod members were selected for metrics.",
                CollectedAtUtc: null,
                Window: null,
                Pods: [],
                Warnings: [],
                TransparencyCommands: []);
        }

        var loadResult = kubeConfigLoader.Load();

        if (loadResult.Contexts.Count is 0)
        {
            throw new ArgumentException("No kube contexts were found.");
        }

        var warnings = new List<KubeQueryWarning>();
        var samples = new List<KubePodMetricsSample>();
        var timestamps = new List<DateTimeOffset>();
        var metricsSources = new HashSet<KubeMetricsSourceKind>();
        string? window = null;
        var anyMetrics = false;

        foreach (var contextGroup in request.Targets
                     .Where(static target => target.Kind is KubeResourceKind.Pod && !string.IsNullOrWhiteSpace(target.Namespace))
                     .GroupBy(static target => target.ContextName, StringComparer.Ordinal))
        {
            var context = KubeResourceQueryService.ResolveTargetContexts([contextGroup.Key], loadResult).Single();

            if (context.Status is KubeContextStatus.ConfigurationError)
            {
                warnings.Add(new KubeQueryWarning(context.Name, context.StatusMessage ?? $"The kube context '{context.Name}' is invalid."));
                continue;
            }

            using var client = kubeConfigLoader.CreateClient(loadResult, context.Name);

            foreach (var namespaceGroup in contextGroup.GroupBy(static target => target.Namespace!, StringComparer.Ordinal))
            {
                V1PodList podList;

                try
                {
                    podList = await client.ListNamespacedPodAsync(namespaceGroup.Key);
                }
                catch (Exception exception)
                {
                    warnings.Add(new KubeQueryWarning(context.Name, $"The pod list for namespace '{namespaceGroup.Key}' could not be loaded: {exception.Message}"));
                    continue;
                }

                var podsByName = (podList?.Items ?? [])
                    .Where(static item => !string.IsNullOrWhiteSpace(item.Metadata?.Name))
                    .ToDictionary(item => item.Metadata!.Name!, StringComparer.Ordinal);
                var usageLookup = await ResolvePodUsageAsync(
                    client,
                    context.Name,
                    namespaceGroup.Key,
                    namespaceGroup.Select(static target => target.Name).ToArray(),
                    cancellationToken);

                if (usageLookup.MetricsSource is not KubeMetricsSourceKind.Unavailable)
                {
                    metricsSources.Add(usageLookup.MetricsSource);
                }

                if (!usageLookup.MetricsAvailable && !string.IsNullOrWhiteSpace(usageLookup.MetricsStatusMessage))
                {
                    warnings.Add(new KubeQueryWarning(context.Name, usageLookup.MetricsStatusMessage));
                }

                foreach (var target in namespaceGroup)
                {
                    if (!usageLookup.UsageByPodName.TryGetValue(target.Name, out var usage))
                    {
                        continue;
                    }

                    podsByName.TryGetValue(target.Name, out var pod);
                    samples.Add(new KubePodMetricsSample(
                        ContextName: context.Name,
                        Namespace: namespaceGroup.Key,
                        PodName: target.Name,
                        RestartCount: pod?.Status?.ContainerStatuses?.Sum(static status => status.RestartCount) ?? 0,
                        CpuMillicores: usage.CpuMillicores,
                        MemoryBytes: usage.MemoryBytes,
                        CollectedAtUtc: usageLookup.CollectedAtUtc,
                        Window: usageLookup.Window));
                    anyMetrics = anyMetrics || usage.CpuMillicores.HasValue || usage.MemoryBytes.HasValue;

                    if (usageLookup.CollectedAtUtc.HasValue)
                    {
                        timestamps.Add(usageLookup.CollectedAtUtc.Value);
                    }

                    window ??= usageLookup.Window;
                }
            }
        }

        return new KubePodMetricsQueryResponse(
            MetricsAvailable: anyMetrics,
            MetricsSource: ResolveCombinedMetricsSource(metricsSources, anyMetrics),
            MetricsStatusMessage: anyMetrics
                ? null
                : (warnings.Count > 0 ? warnings[0].Message : "Live metrics are not currently available for the requested pod members."),
            CollectedAtUtc: timestamps.Count > 0 ? timestamps.Max() : null,
            Window: window,
            Pods: samples
                .OrderByDescending(static sample => sample.CpuMillicores ?? -1)
                .ThenByDescending(static sample => sample.MemoryBytes ?? -1)
                .ThenBy(static sample => sample.Namespace, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static sample => sample.PodName, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Warnings: warnings,
            TransparencyCommands: DecorateMetricsTransparencyCommands(
                KubectlTransparencyFactory.CreateForPodMetricsQuery(request.Targets),
                ResolveCombinedMetricsSource(metricsSources, anyMetrics)));
    }

    private static string RequireNamespace(KubeResourceMetricsRequest request)
    {
        return string.IsNullOrWhiteSpace(request.Namespace)
            ? throw new ArgumentException($"A namespace is required for {request.Kind} resources.", nameof(request))
            : request.Namespace.Trim();
    }

    private async Task<KubeResourceMetricsResponse> GetPodMetricsAsync(
        Kubernetes client,
        string contextName,
        string namespaceName,
        string name,
        CancellationToken cancellationToken)
    {
        var pod = await client.ReadNamespacedPodAsync(name, namespaceName, cancellationToken: cancellationToken);
        var contributor = CreateContributor(pod);
        var usageSnapshot = await ResolvePodUsageAsync(client, contextName, namespaceName, [name], cancellationToken);
        usageSnapshot.UsageByPodName.TryGetValue(name, out var usage);

        return BuildResponse(
            kind: KubeResourceKind.Pod,
            contextName: contextName,
            namespaceName: namespaceName,
            name: name,
            contributors: [contributor with
            {
                CpuMillicores = usage?.CpuMillicores,
                MemoryBytes = usage?.MemoryBytes
            }],
            metricsAvailable: usageSnapshot.MetricsAvailable,
            metricsSource: usageSnapshot.MetricsSource,
            metricsStatusMessage: usageSnapshot.MetricsStatusMessage,
            collectedAtUtc: usageSnapshot.CollectedAtUtc,
            window: usageSnapshot.Window,
            warnings: string.IsNullOrWhiteSpace(usageSnapshot.MetricsStatusMessage) || usageSnapshot.MetricsAvailable
                ? []
                : [new KubeQueryWarning(contextName, usageSnapshot.MetricsStatusMessage)],
            schedulingPressure: GetPodSchedulingPressure(pod),
            transparencyCommands: DecorateMetricsTransparencyCommands(
                KubectlTransparencyFactory.CreateForResourceMetrics(
                    KubeResourceKind.Pod,
                    contextName,
                    namespaceName,
                    name,
                    [name]),
                usageSnapshot.MetricsSource));
    }

    private async Task<KubeResourceMetricsResponse> GetDeploymentMetricsAsync(
        Kubernetes client,
        string contextName,
        string namespaceName,
        string name,
        CancellationToken cancellationToken)
    {
        var deployment = await client.ReadNamespacedDeploymentAsync(name, namespaceName, cancellationToken: cancellationToken);
        var pods = await ListPodsBySelectorAsync(client, namespaceName, deployment.Spec?.Selector?.MatchLabels, cancellationToken);
        return await BuildPodBackedMetricsResponseAsync(client, contextName, KubeResourceKind.Deployment, namespaceName, name, pods, cancellationToken);
    }

    private async Task<KubeResourceMetricsResponse> GetReplicaSetMetricsAsync(
        Kubernetes client,
        string contextName,
        string namespaceName,
        string name,
        CancellationToken cancellationToken)
    {
        var replicaSet = await client.ReadNamespacedReplicaSetAsync(name, namespaceName, cancellationToken: cancellationToken);
        var pods = await ListPodsBySelectorAsync(client, namespaceName, replicaSet.Spec?.Selector?.MatchLabels, cancellationToken);
        return await BuildPodBackedMetricsResponseAsync(client, contextName, KubeResourceKind.ReplicaSet, namespaceName, name, pods, cancellationToken);
    }

    private async Task<KubeResourceMetricsResponse> GetStatefulSetMetricsAsync(
        Kubernetes client,
        string contextName,
        string namespaceName,
        string name,
        CancellationToken cancellationToken)
    {
        var statefulSet = await client.ReadNamespacedStatefulSetAsync(name, namespaceName, cancellationToken: cancellationToken);
        var pods = await ListPodsBySelectorAsync(client, namespaceName, statefulSet.Spec?.Selector?.MatchLabels, cancellationToken);
        return await BuildPodBackedMetricsResponseAsync(client, contextName, KubeResourceKind.StatefulSet, namespaceName, name, pods, cancellationToken);
    }

    private async Task<KubeResourceMetricsResponse> GetDaemonSetMetricsAsync(
        Kubernetes client,
        string contextName,
        string namespaceName,
        string name,
        CancellationToken cancellationToken)
    {
        var daemonSet = await client.ReadNamespacedDaemonSetAsync(name, namespaceName, cancellationToken: cancellationToken);
        var pods = await ListPodsBySelectorAsync(client, namespaceName, daemonSet.Spec?.Selector?.MatchLabels, cancellationToken);
        return await BuildPodBackedMetricsResponseAsync(client, contextName, KubeResourceKind.DaemonSet, namespaceName, name, pods, cancellationToken);
    }

    private async Task<KubeResourceMetricsResponse> GetServiceMetricsAsync(
        Kubernetes client,
        string contextName,
        string namespaceName,
        string name,
        CancellationToken cancellationToken)
    {
        var service = await client.ReadNamespacedServiceAsync(name, namespaceName, cancellationToken: cancellationToken);
        var pods = await ListPodsBySelectorAsync(client, namespaceName, service.Spec?.Selector, cancellationToken);
        return await BuildPodBackedMetricsResponseAsync(client, contextName, KubeResourceKind.Service, namespaceName, name, pods, cancellationToken);
    }

    private async Task<KubeResourceMetricsResponse> GetNodeMetricsAsync(
        Kubernetes client,
        string contextName,
        string name,
        CancellationToken cancellationToken)
    {
        var node = await client.ReadNodeAsync(name, cancellationToken: cancellationToken);
        NodeMetricsList? metricsList = null;
        string? warning = null;

        try
        {
            metricsList = await client.GetKubernetesNodesMetricsAsync();
        }
        catch (Exception exception) when (IsMetricsUnavailable(exception))
        {
            warning = BuildMetricsUnavailableMessage(exception);
        }

        var nodeMetrics = metricsList?.Items?.FirstOrDefault(item => string.Equals(item.Metadata?.Name, name, StringComparison.Ordinal));
        var cpuMillicores = TryGetCpuMillicores(nodeMetrics?.Usage);
        var memoryBytes = TryGetMemoryBytes(nodeMetrics?.Usage);
        var readyCondition = node.Status?.Conditions?.FirstOrDefault(condition => string.Equals(condition.Type, "Ready", StringComparison.OrdinalIgnoreCase));
        var schedulingPressure = BuildNodeSchedulingPressure(node);
        var collectedAtUtc = NormalizeTimestamp(nodeMetrics?.Timestamp);

        return new KubeResourceMetricsResponse(
            Kind: KubeResourceKind.Node,
            ContextName: contextName,
            Name: name,
            Namespace: null,
            MetricsAvailable: cpuMillicores.HasValue || memoryBytes.HasValue,
            MetricsSource: cpuMillicores.HasValue || memoryBytes.HasValue
                ? KubeMetricsSourceKind.KubernetesMetricsApi
                : KubeMetricsSourceKind.Unavailable,
            MetricsStatusMessage: cpuMillicores.HasValue || memoryBytes.HasValue ? null : warning ?? "Live node usage is not currently available.",
            CpuMillicores: cpuMillicores,
            MemoryBytes: memoryBytes,
            ContributorCount: 1,
            UnhealthyContributorCount: string.Equals(readyCondition?.Status, "True", StringComparison.OrdinalIgnoreCase) ? 0 : 1,
            RestartCount: 0,
            SchedulingPressure: schedulingPressure,
            CollectedAtUtc: collectedAtUtc,
            Window: nodeMetrics?.Window,
            Contributors: [],
            Warnings: warning is null ? [] : [new KubeQueryWarning(contextName, warning)],
            TransparencyCommands: KubectlTransparencyFactory.CreateForResourceMetrics(
                KubeResourceKind.Node,
                contextName,
                null,
                name,
                []));
    }

    private static KubeResourceMetricsResponse CreateUnsupportedKindResponse(KubeResourceMetricsRequest request, string contextName)
    {
        return new KubeResourceMetricsResponse(
            Kind: request.Kind,
            ContextName: contextName,
            Name: request.Name.Trim(),
            Namespace: string.IsNullOrWhiteSpace(request.Namespace) ? null : request.Namespace.Trim(),
            MetricsAvailable: false,
            MetricsSource: KubeMetricsSourceKind.Unavailable,
            MetricsStatusMessage: "Live usage is not available for this resource kind yet.",
            CpuMillicores: null,
            MemoryBytes: null,
            ContributorCount: 0,
            UnhealthyContributorCount: 0,
            RestartCount: 0,
            SchedulingPressure: null,
            CollectedAtUtc: null,
            Window: null,
            Contributors: [],
            Warnings: [],
            TransparencyCommands: KubectlTransparencyFactory.CreateForResourceMetrics(
                request.Kind,
                contextName,
                request.Namespace,
                request.Name.Trim(),
                []));
    }

    private async Task<KubeResourceMetricsResponse> BuildPodBackedMetricsResponseAsync(
        Kubernetes client,
        string contextName,
        KubeResourceKind kind,
        string namespaceName,
        string name,
        IReadOnlyList<V1Pod> pods,
        CancellationToken cancellationToken)
    {
        var contributors = pods.Select(CreateContributor).ToArray();
        var podNames = pods
            .Select(static pod => pod.Metadata?.Name)
            .Where(static podName => !string.IsNullOrWhiteSpace(podName))
            .Cast<string>()
            .ToArray();
        var usageLookup = await ResolvePodUsageAsync(client, contextName, namespaceName, podNames, cancellationToken);

        var enrichedContributors = contributors
            .Select(contributor =>
            {
                usageLookup.UsageByPodName.TryGetValue(contributor.Name, out var usage);

                return contributor with
                {
                    CpuMillicores = usage?.CpuMillicores,
                    MemoryBytes = usage?.MemoryBytes
                };
            })
            .OrderByDescending(static contributor => contributor.CpuMillicores ?? -1)
            .ThenByDescending(static contributor => contributor.MemoryBytes ?? -1)
            .ThenBy(static contributor => contributor.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return BuildResponse(
            kind,
            contextName,
            namespaceName,
            name,
            enrichedContributors,
            usageLookup.MetricsAvailable,
            usageLookup.MetricsSource,
            usageLookup.MetricsStatusMessage ?? (usageLookup.MetricsAvailable ? null : "Live workload usage is not currently available."),
            usageLookup.CollectedAtUtc,
            usageLookup.Window,
            string.IsNullOrWhiteSpace(usageLookup.MetricsStatusMessage) || usageLookup.MetricsAvailable
                ? []
                : [new KubeQueryWarning(contextName, usageLookup.MetricsStatusMessage)],
            BuildPodSchedulingPressure(pods),
            DecorateMetricsTransparencyCommands(
                KubectlTransparencyFactory.CreateForResourceMetrics(
                    kind,
                    contextName,
                    namespaceName,
                    name,
                    podNames),
                usageLookup.MetricsSource));
    }

    private static KubeResourceMetricsResponse BuildResponse(
        KubeResourceKind kind,
        string contextName,
        string? namespaceName,
        string name,
        IReadOnlyList<KubeResourceMetricsContributor> contributors,
        bool metricsAvailable,
        KubeMetricsSourceKind metricsSource,
        string? metricsStatusMessage,
        DateTimeOffset? collectedAtUtc,
        string? window,
        IReadOnlyList<KubeQueryWarning> warnings,
        string? schedulingPressure,
        IReadOnlyList<KubectlCommandPreview> transparencyCommands)
    {
        var cpuMillicores = contributors.Sum(static contributor => contributor.CpuMillicores ?? 0);
        var memoryBytes = contributors.Sum(static contributor => contributor.MemoryBytes ?? 0);

        return new KubeResourceMetricsResponse(
            Kind: kind,
            ContextName: contextName,
            Name: name,
            Namespace: namespaceName,
            MetricsAvailable: metricsAvailable,
            MetricsSource: metricsSource,
            MetricsStatusMessage: metricsStatusMessage,
            CpuMillicores: metricsAvailable ? cpuMillicores : null,
            MemoryBytes: metricsAvailable ? memoryBytes : null,
            ContributorCount: contributors.Count,
            UnhealthyContributorCount: contributors.Count(static contributor => !contributor.Healthy),
            RestartCount: contributors.Sum(static contributor => contributor.RestartCount),
            SchedulingPressure: schedulingPressure,
            CollectedAtUtc: collectedAtUtc,
            Window: window,
            Contributors: contributors,
            Warnings: warnings,
            TransparencyCommands: transparencyCommands);
    }

    private static async Task<IReadOnlyList<V1Pod>> ListPodsBySelectorAsync(
        Kubernetes client,
        string namespaceName,
        IEnumerable<KeyValuePair<string, string>>? selector,
        CancellationToken cancellationToken)
    {
        var labelSelector = CreateLabelSelector(selector);

        if (string.IsNullOrWhiteSpace(labelSelector))
        {
            return [];
        }

        var pods = await client.ListNamespacedPodAsync(
            namespaceName,
            labelSelector: labelSelector,
            cancellationToken: cancellationToken);

        return pods.Items.ToArray();
    }

    private static string? CreateLabelSelector(IEnumerable<KeyValuePair<string, string>>? selector)
    {
        if (selector is null)
        {
            return null;
        }

        var parts = selector
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => $"{pair.Key}={pair.Value}")
            .ToArray();

        return parts.Length is 0 ? null : string.Join(",", parts);
    }

    private static KubeResourceMetricsContributor CreateContributor(V1Pod pod)
    {
        var containerStatuses = pod.Status?.ContainerStatuses ?? [];
        var restartCount = containerStatuses.Sum(static status => status.RestartCount);
        var allContainersReady = containerStatuses.Count is 0 || containerStatuses.All(static status => status.Ready);
        var healthy = string.Equals(pod.Status?.Phase, "Running", StringComparison.OrdinalIgnoreCase) && allContainersReady;

        return new KubeResourceMetricsContributor(
            Name: pod.Metadata?.Name ?? string.Empty,
            Namespace: pod.Metadata?.NamespaceProperty,
            Status: pod.Status?.Phase ?? pod.Status?.Reason,
            Healthy: healthy,
            RestartCount: restartCount,
            CpuMillicores: null,
            MemoryBytes: null);
    }

    private async Task<PodUsageLookupResult> ResolvePodUsageAsync(
        Kubernetes client,
        string contextName,
        string namespaceName,
        IReadOnlyCollection<string> podNames,
        CancellationToken cancellationToken)
    {
        var metricsSourceMode = runtimeOptions.Metrics.SourceMode;

        if (metricsSourceMode is KubeMetricsSourceMode.PrometheusOnly)
        {
            var prometheusOnlyResult = await QueryPrometheusAsync(namespaceName, podNames, cancellationToken);
            return prometheusOnlyResult.MetricsAvailable
                ? prometheusOnlyResult
                : prometheusOnlyResult with
                {
                    MetricsStatusMessage = prometheusOnlyResult.MetricsStatusMessage ?? "Prometheus-only metrics mode is enabled, but no live Prometheus pod usage is available."
                };
        }

        if (metricsSourceMode is KubeMetricsSourceMode.PrometheusPreferred)
        {
            var prometheusResult = await QueryPrometheusAsync(namespaceName, podNames, cancellationToken);

            if (prometheusResult.MetricsAvailable)
            {
                return prometheusResult;
            }

            var metricsApiResult = await QueryMetricsApiAsync(client, namespaceName, cancellationToken);
            return metricsApiResult.MetricsAvailable
                ? metricsApiResult
                : MergeUnavailableMetricsResults(prometheusResult, metricsApiResult);
        }

        if (metricsSourceMode is KubeMetricsSourceMode.MetricsApiOnly)
        {
            return await QueryMetricsApiAsync(client, namespaceName, cancellationToken);
        }

        var primaryResult = await QueryMetricsApiAsync(client, namespaceName, cancellationToken);

        if (primaryResult.MetricsAvailable)
        {
            return primaryResult;
        }

        var fallbackResult = await QueryPrometheusAsync(namespaceName, podNames, cancellationToken);
        return fallbackResult.MetricsAvailable
            ? fallbackResult
            : MergeUnavailableMetricsResults(primaryResult, fallbackResult);
    }

    private async Task<PodUsageLookupResult> QueryMetricsApiAsync(
        Kubernetes client,
        string namespaceName,
        CancellationToken cancellationToken)
    {
        try
        {
            var metricsList = await client.GetKubernetesPodsMetricsByNamespaceAsync(namespaceName);
            var metricsByName = (metricsList?.Items ?? [])
                .Where(static item => !string.IsNullOrWhiteSpace(item.Metadata?.Name))
                .ToDictionary(
                    item => item.Metadata!.Name!,
                    item =>
                    {
                        var usage = SumContainerUsage(item.Containers);
                        return new PrometheusPodUsage(usage.CpuMillicores, usage.MemoryBytes);
                    },
                    StringComparer.Ordinal);
            var collectedAtUtc = (metricsList?.Items ?? [])
                .Select(static item => NormalizeTimestamp(item.Timestamp))
                .Where(static timestamp => timestamp.HasValue)
                .Select(static timestamp => timestamp!.Value)
                .DefaultIfEmpty()
                .Max();
            DateTimeOffset? resolvedCollectedAtUtc = collectedAtUtc == default
                ? null
                : collectedAtUtc;
            var window = (metricsList?.Items ?? [])
                .Select(static item => item.Window)
                .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

            return new PodUsageLookupResult(
                MetricsAvailable: metricsByName.Values.Any(static usage => usage.CpuMillicores.HasValue || usage.MemoryBytes.HasValue),
                MetricsSource: KubeMetricsSourceKind.KubernetesMetricsApi,
                MetricsStatusMessage: null,
                CollectedAtUtc: resolvedCollectedAtUtc,
                Window: window,
                UsageByPodName: metricsByName);
        }
        catch (Exception exception) when (IsMetricsUnavailable(exception))
        {
            return new PodUsageLookupResult(
                MetricsAvailable: false,
                MetricsSource: KubeMetricsSourceKind.Unavailable,
                MetricsStatusMessage: BuildMetricsUnavailableMessage(exception),
                CollectedAtUtc: null,
                Window: null,
                UsageByPodName: new Dictionary<string, PrometheusPodUsage>(StringComparer.Ordinal));
        }
    }

    private async Task<PodUsageLookupResult> QueryPrometheusAsync(
        string namespaceName,
        IReadOnlyCollection<string> podNames,
        CancellationToken cancellationToken)
    {
        var result = await prometheusMetricsSource.QueryPodUsageAsync(namespaceName, podNames, cancellationToken);
        return new PodUsageLookupResult(
            MetricsAvailable: result.MetricsAvailable,
            MetricsSource: result.MetricsAvailable ? KubeMetricsSourceKind.Prometheus : KubeMetricsSourceKind.Unavailable,
            MetricsStatusMessage: result.FailureMessage,
            CollectedAtUtc: result.CollectedAtUtc,
            Window: result.Window,
            UsageByPodName: result.UsageByPod);
    }

    private static PodUsageLookupResult MergeUnavailableMetricsResults(PodUsageLookupResult primary, PodUsageLookupResult fallback)
    {
        var messageParts = new[]
        {
            primary.MetricsStatusMessage,
            fallback.MetricsStatusMessage
        }
        .Where(static message => !string.IsNullOrWhiteSpace(message))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

        return new PodUsageLookupResult(
            MetricsAvailable: false,
            MetricsSource: KubeMetricsSourceKind.Unavailable,
            MetricsStatusMessage: messageParts.Length is 0 ? "Live pod metrics are unavailable from the configured sources." : string.Join(" ", messageParts),
            CollectedAtUtc: primary.CollectedAtUtc ?? fallback.CollectedAtUtc,
            Window: primary.Window ?? fallback.Window,
            UsageByPodName: fallback.UsageByPodName.Count > 0 ? fallback.UsageByPodName : primary.UsageByPodName);
    }

    private static KubeMetricsSourceKind ResolveCombinedMetricsSource(IReadOnlyCollection<KubeMetricsSourceKind> metricsSources, bool metricsAvailable)
    {
        if (!metricsAvailable || metricsSources.Count is 0)
        {
            return KubeMetricsSourceKind.Unavailable;
        }

        return metricsSources.Count is 1
            ? metricsSources.Single()
            : KubeMetricsSourceKind.Mixed;
    }

    private static IReadOnlyList<KubectlCommandPreview> DecorateMetricsTransparencyCommands(
        IReadOnlyList<KubectlCommandPreview> commands,
        KubeMetricsSourceKind metricsSource)
    {
        var note = metricsSource switch
        {
            KubeMetricsSourceKind.Prometheus => "Live values came from Prometheus. `kubectl top` is the nearest equivalent only when metrics-server is present for this cluster.",
            KubeMetricsSourceKind.Mixed => "Live values came from mixed sources. `kubectl top` is the nearest equivalent for members resolved through the Kubernetes metrics API.",
            _ => null
        };

        if (string.IsNullOrWhiteSpace(note))
        {
            return commands;
        }

        return commands
            .Select(command => command with
            {
                Notes = string.IsNullOrWhiteSpace(command.Notes)
                    ? note
                    : $"{command.Notes} {note}"
            })
            .ToArray();
    }

    private static (long? CpuMillicores, long? MemoryBytes) SumContainerUsage(IEnumerable<ContainerMetrics>? containers)
    {
        var cpuValues = containers?
            .Select(static container => TryGetCpuMillicores(container.Usage))
            .Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .ToArray() ?? [];
        var memoryValues = containers?
            .Select(static container => TryGetMemoryBytes(container.Usage))
            .Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .ToArray() ?? [];

        return (
            cpuValues.Length is 0 ? null : cpuValues.Sum(),
            memoryValues.Length is 0 ? null : memoryValues.Sum());
    }

    private static long? TryGetCpuMillicores(IDictionary<string, ResourceQuantity>? usage)
    {
        if (usage is null || !usage.TryGetValue("cpu", out var quantity))
        {
            return null;
        }

        return KubeMetricsQuantityParser.ParseCpuMillicores(quantity?.ToString());
    }

    private static long? TryGetMemoryBytes(IDictionary<string, ResourceQuantity>? usage)
    {
        if (usage is null || !usage.TryGetValue("memory", out var quantity))
        {
            return null;
        }

        return KubeMetricsQuantityParser.ParseBytes(quantity?.ToString());
    }

    private static string? BuildPodSchedulingPressure(IReadOnlyList<V1Pod> pods)
    {
        var unschedulableCount = pods.Count(static pod => string.Equals(GetPodSchedulingPressure(pod), "Unschedulable", StringComparison.Ordinal));
        var pendingCount = pods.Count(static pod => string.Equals(pod.Status?.Phase, "Pending", StringComparison.OrdinalIgnoreCase));

        if (unschedulableCount > 0)
        {
            return unschedulableCount == 1
                ? "1 pod is unschedulable."
                : $"{unschedulableCount} pods are unschedulable.";
        }

        if (pendingCount > 0)
        {
            return pendingCount == 1
                ? "1 pod is still pending scheduling."
                : $"{pendingCount} pods are still pending scheduling.";
        }

        return null;
    }

    private static string? GetPodSchedulingPressure(V1Pod pod)
    {
        var scheduledCondition = pod.Status?.Conditions?
            .FirstOrDefault(condition => string.Equals(condition.Type, "PodScheduled", StringComparison.OrdinalIgnoreCase));

        if (scheduledCondition is not null &&
            string.Equals(scheduledCondition.Status, "False", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(scheduledCondition.Reason, "Unschedulable", StringComparison.OrdinalIgnoreCase))
        {
            return "Unschedulable";
        }

        if (string.Equals(pod.Status?.Phase, "Pending", StringComparison.OrdinalIgnoreCase))
        {
            return "Pending";
        }

        return null;
    }

    private static string? BuildNodeSchedulingPressure(V1Node node)
    {
        var conditions = node.Status?.Conditions ?? [];
        var pressureConditions = conditions
            .Where(static condition =>
                string.Equals(condition.Status, "True", StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(condition.Type, "MemoryPressure", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(condition.Type, "DiskPressure", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(condition.Type, "PIDPressure", StringComparison.OrdinalIgnoreCase)))
            .Select(static condition => condition.Type)
            .ToArray();

        if (pressureConditions.Length > 0)
        {
            return string.Join(", ", pressureConditions);
        }

        return node.Spec?.Unschedulable is true
            ? "Node scheduling is disabled."
            : null;
    }

    private static bool IsMetricsUnavailable(Exception exception)
    {
        return exception is HttpOperationException or k8s.Autorest.HttpOperationException;
    }

    private static DateTimeOffset? NormalizeTimestamp(DateTimeOffset? value)
    {
        return value;
    }

    private static DateTimeOffset? NormalizeTimestamp(DateTime? value)
    {
        if (!value.HasValue)
        {
            return null;
        }

        var timestamp = value.Value;
        var utcTimestamp = timestamp.Kind switch
        {
            DateTimeKind.Unspecified => DateTime.SpecifyKind(timestamp, DateTimeKind.Utc),
            DateTimeKind.Local => timestamp.ToUniversalTime(),
            _ => timestamp
        };

        return new DateTimeOffset(utcTimestamp, TimeSpan.Zero);
    }

    private static string BuildMetricsUnavailableMessage(Exception exception)
    {
        return exception.Message.Contains("metrics", StringComparison.OrdinalIgnoreCase)
            ? exception.Message
            : "The Kubernetes metrics API is not currently available for this cluster.";
    }
}

internal sealed record PodUsageLookupResult(
    bool MetricsAvailable,
    KubeMetricsSourceKind MetricsSource,
    string? MetricsStatusMessage,
    DateTimeOffset? CollectedAtUtc,
    string? Window,
    IReadOnlyDictionary<string, PrometheusPodUsage> UsageByPodName);
