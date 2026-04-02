using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Kube;

public sealed class KubeWorkspaceResolveService(KubeContextDiscoveryService discoveryService)
{
    public async Task<KubeWorkspaceResolveResponse> ResolveAsync(
        KubeWorkspaceResolveRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var contextsResponse = await discoveryService.GetContextsAsync(cancellationToken);
        var warnings = contextsResponse.Warnings.ToList();
        var contextsByName = contextsResponse.Contexts.ToDictionary(context => context.Name, StringComparer.Ordinal);
        var requestedContextNames = request.Contexts
            .Where(static contextName => !string.IsNullOrWhiteSpace(contextName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var missingContexts = new List<string>();
        var unavailableContexts = new List<KubeContextSummary>();
        var resolvedContexts = new List<KubeContextSummary>();

        foreach (var requestedContextName in requestedContextNames)
        {
            if (!contextsByName.TryGetValue(requestedContextName, out var context))
            {
                missingContexts.Add(requestedContextName);
                warnings.Add($"Requested workspace context '{requestedContextName}' is no longer present in kubeconfig.");
                continue;
            }

            if (context.Status is KubeContextStatus.Configured)
            {
                resolvedContexts.Add(context);
                continue;
            }

            unavailableContexts.Add(context);
            warnings.Add(
                string.IsNullOrWhiteSpace(context.StatusMessage)
                    ? $"Requested workspace context '{context.Name}' is not currently queryable."
                    : $"Requested workspace context '{context.Name}' is not currently queryable: {context.StatusMessage}");
        }

        var fallbackContext = ResolveFallbackContext(contextsResponse);
        var usedCurrentContextFallback = false;

        if (requestedContextNames.Length is 0 && fallbackContext is not null)
        {
            resolvedContexts.Add(fallbackContext);
        }
        else if (requestedContextNames.Length > 0 && resolvedContexts.Count is 0 && fallbackContext is not null)
        {
            resolvedContexts.Add(fallbackContext);
            usedCurrentContextFallback = true;
            warnings.Add($"Fell back to '{fallbackContext.Name}' because none of the requested workspace contexts are queryable.");
        }

        var normalizedNamespace = NormalizeOptionalText(request.Namespace);
        var ignoredNamespaceFilter = false;

        if (!request.IncludeAllSupportedKinds &&
            !string.IsNullOrWhiteSpace(normalizedNamespace) &&
            IsClusterScopedRequest(request.Kind, request.CustomResourceType))
        {
            normalizedNamespace = null;
            ignoredNamespaceFilter = true;
            var resourceLabel = request.Kind is KubeResourceKind.CustomResource
                ? request.CustomResourceType?.Kind ?? request.Kind.ToString()
                : request.Kind.ToString();
            warnings.Add($"Namespace filters do not apply to cluster-scoped {resourceLabel} workspaces, so the namespace filter was cleared.");
        }

        var resolvedQuery = new KubeResourceQueryRequest
        {
            Kind = request.Kind,
            CustomResourceType = request.CustomResourceType,
            IncludeAllSupportedKinds = request.IncludeAllSupportedKinds,
            Contexts = resolvedContexts.Select(context => context.Name).ToArray(),
            Namespace = normalizedNamespace,
            Search = NormalizeOptionalText(request.Search),
            Limit = KubeResourceQueryService.NormalizeLimit(request.Limit)
        };

        var resolvedKinds = KubeResourceQueryService.ResolveTargetKinds(resolvedQuery);
        var resolvedClusters = KubeClusterSummaryFactory.Build(resolvedContexts);

        return new KubeWorkspaceResolveResponse(
            ResolvedQuery: resolvedQuery,
            CurrentContextName: contextsResponse.CurrentContextName,
            ResolvedContexts: resolvedContexts,
            UnavailableContexts: unavailableContexts,
            MissingContexts: missingContexts,
            ResolvedKinds: resolvedKinds,
            Warnings: warnings,
            UsedCurrentContextFallback: usedCurrentContextFallback,
            IgnoredNamespaceFilter: ignoredNamespaceFilter,
            ScopeSummary: BuildScopeSummary(resolvedQuery, resolvedKinds, resolvedClusters))
        {
            ResolvedClusters = resolvedClusters
        };
    }

    private static KubeContextSummary? ResolveFallbackContext(KubeContextsResponse contextsResponse)
    {
        if (!string.IsNullOrWhiteSpace(contextsResponse.CurrentContextName))
        {
            var currentQueryableContext = contextsResponse.Contexts.FirstOrDefault(context =>
                context.Status is KubeContextStatus.Configured &&
                string.Equals(context.Name, contextsResponse.CurrentContextName, StringComparison.Ordinal));

            if (currentQueryableContext is not null)
            {
                return currentQueryableContext;
            }
        }

        return contextsResponse.Contexts.FirstOrDefault(context => context.Status is KubeContextStatus.Configured);
    }

    private static string? NormalizeOptionalText(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }

    private static bool IsClusterScopedKind(KubeResourceKind kind)
    {
        return kind is KubeResourceKind.Namespace or KubeResourceKind.Node;
    }

    private static bool IsClusterScopedRequest(KubeResourceKind kind, KubeCustomResourceType? customResourceType)
    {
        return kind switch
        {
            KubeResourceKind.CustomResource => customResourceType?.Namespaced is false,
            _ => IsClusterScopedKind(kind)
        };
    }

    private static string BuildScopeSummary(
        KubeResourceQueryRequest resolvedQuery,
        IReadOnlyList<KubeResourceKind> resolvedKinds,
        IReadOnlyList<KubeClusterSummary> resolvedClusters)
    {
        if (resolvedQuery.Contexts.Count is 0)
        {
            return "No queryable workspace scope is currently available.";
        }

        var kindText = resolvedQuery.IncludeAllSupportedKinds
            ? "All supported kinds"
            : resolvedQuery.Kind.ToString();
        var contextText = resolvedQuery.Contexts.Count is 1
            ? resolvedQuery.Contexts[0]
            : $"{resolvedQuery.Contexts.Count} contexts";
        var clusterText = resolvedClusters.Count switch
        {
            0 => null,
            1 when resolvedQuery.Contexts.Count is 1 => null,
            1 => $"on cluster {resolvedClusters[0].Name}",
            _ => $"on {resolvedClusters.Count} clusters"
        };
        var scopeText = !string.IsNullOrWhiteSpace(resolvedQuery.Namespace)
            ? $"namespace {resolvedQuery.Namespace}"
            : resolvedKinds.Count is 1 && IsClusterScopedKind(resolvedKinds[0])
                ? "cluster scope"
                : "all namespaces";

        return resolvedQuery.Contexts.Count is 1
            ? $"{kindText} on {contextText}{(clusterText is null ? string.Empty : $" {clusterText}")} in {scopeText}."
            : $"{kindText} across {contextText}{(clusterText is null ? string.Empty : $" {clusterText}")} in {scopeText}.";
    }
}
