using k8s;
using k8s.Autorest;
using k8s.Models;
using Kuberkynesis.Ui.Shared.Kubernetes;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kuberkynesis.Agent.Kube;

public sealed class KubeResourceDetailService
{
    private const int RelatedResourceLimit = 25;

    private readonly IKubeConfigLoader kubeConfigLoader;

    public KubeResourceDetailService(IKubeConfigLoader kubeConfigLoader)
    {
        this.kubeConfigLoader = kubeConfigLoader;
    }

    public async Task<KubeResourceDetailResponse> GetDetailAsync(KubeResourceDetailRequest request, CancellationToken cancellationToken)
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
            KubeResourceKind.CustomResource => await GetCustomResourceDetailAsync(client, context.Name, request, cancellationToken),
            KubeResourceKind.Namespace => await GetNamespaceDetailAsync(client, context.Name, request.Name, cancellationToken),
            KubeResourceKind.Node => await GetNodeDetailAsync(client, context.Name, request.Name, cancellationToken),
            KubeResourceKind.Pod => await GetPodDetailAsync(client, context.Name, RequireNamespace(request), request.Name, cancellationToken),
            KubeResourceKind.Deployment => await GetDeploymentDetailAsync(client, context.Name, RequireNamespace(request), request.Name, cancellationToken),
            KubeResourceKind.ReplicaSet => await GetReplicaSetDetailAsync(client, context.Name, RequireNamespace(request), request.Name, cancellationToken),
            KubeResourceKind.StatefulSet => await GetStatefulSetDetailAsync(client, context.Name, RequireNamespace(request), request.Name, cancellationToken),
            KubeResourceKind.DaemonSet => await GetDaemonSetDetailAsync(client, context.Name, RequireNamespace(request), request.Name, cancellationToken),
            KubeResourceKind.Service => await GetServiceDetailAsync(client, context.Name, RequireNamespace(request), request.Name, cancellationToken),
            KubeResourceKind.Ingress => await GetIngressDetailAsync(client, context.Name, RequireNamespace(request), request.Name, cancellationToken),
            KubeResourceKind.ConfigMap => await GetConfigMapDetailAsync(client, context.Name, RequireNamespace(request), request.Name, cancellationToken),
            KubeResourceKind.Secret => await GetSecretDetailAsync(client, context.Name, RequireNamespace(request), request.Name, cancellationToken),
            KubeResourceKind.Job => await GetJobDetailAsync(client, context.Name, RequireNamespace(request), request.Name, cancellationToken),
            KubeResourceKind.CronJob => await GetCronJobDetailAsync(client, context.Name, RequireNamespace(request), request.Name, cancellationToken),
            KubeResourceKind.Event => await GetEventDetailAsync(client, context.Name, RequireNamespace(request), request.Name, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(request.Kind), request.Kind, "Unsupported Kubernetes resource kind.")
        };
    }

    private static async Task<KubeResourceDetailResponse> GetCustomResourceDetailAsync(
        Kubernetes client,
        string contextName,
        KubeResourceDetailRequest request,
        CancellationToken cancellationToken)
    {
        var customResourceType = request.CustomResourceType
            ?? throw new ArgumentException("A custom resource definition is required for custom resource inspection.", nameof(request));
        var operations = (ICustomObjectsOperations)client;

        object response = customResourceType.Namespaced
            ? await k8s.CustomObjectsOperationsExtensions.GetNamespacedCustomObjectAsync(
                operations,
                customResourceType.Group,
                customResourceType.Version,
                request.Namespace ?? throw new ArgumentException("A namespace is required for this custom resource.", nameof(request)),
                customResourceType.Plural,
                request.Name,
                cancellationToken: cancellationToken)
            : await k8s.CustomObjectsOperationsExtensions.GetClusterCustomObjectAsync(
                operations,
                customResourceType.Group,
                customResourceType.Version,
                customResourceType.Plural,
                request.Name,
                cancellationToken: cancellationToken);

        var node = JsonSerializer.SerializeToNode(response) as JsonObject
            ?? throw new InvalidOperationException("The custom resource detail could not be parsed.");

        return WithTransparency(
            KubeResourceDetailFactory.Create(contextName, customResourceType, node),
            request);
    }

    private static string RequireNamespace(KubeResourceDetailRequest request)
    {
        return string.IsNullOrWhiteSpace(request.Namespace)
            ? throw new ArgumentException($"A namespace is required for {request.Kind} resources.", nameof(request))
            : request.Namespace.Trim();
    }

    private static async Task<KubeResourceDetailResponse> GetNamespaceDetailAsync(
        Kubernetes client,
        string contextName,
        string name,
        CancellationToken cancellationToken)
    {
        var resource = await client.ReadNamespaceAsync(name, cancellationToken: cancellationToken);
        return WithTransparency(
            KubeResourceDetailFactory.Create(contextName, resource),
            new KubeResourceDetailRequest
            {
                ContextName = contextName,
                Kind = KubeResourceKind.Namespace,
                Name = name
            });
    }

    private static async Task<KubeResourceDetailResponse> GetNodeDetailAsync(
        Kubernetes client,
        string contextName,
        string name,
        CancellationToken cancellationToken)
    {
        var resource = await client.ReadNodeAsync(name, cancellationToken: cancellationToken);
        var relatedPodsResult = await TryLoadOptionalRelatedResourcesAsync(
            contextName,
            $"scheduled pods for node '{name}'",
            () => ListPodsForNodeAsync(client, contextName, name, cancellationToken));

        return WithTransparency(
            KubeResourceDetailFactory.Create(contextName, resource, relatedPodsResult.RelatedResources) with
            {
                Warnings = relatedPodsResult.Warnings
            },
            new KubeResourceDetailRequest
            {
                ContextName = contextName,
                Kind = KubeResourceKind.Node,
                Name = name
            });
    }

    private static async Task<KubeResourceDetailResponse> GetPodDetailAsync(
        Kubernetes client,
        string contextName,
        string namespaceName,
        string name,
        CancellationToken cancellationToken)
    {
        var resource = await client.ReadNamespacedPodAsync(name, namespaceName, cancellationToken: cancellationToken);
        var selectedServicesResult = await TryLoadOptionalRelatedResourcesAsync(
            contextName,
            $"service relationships for pod '{name}'",
            () => ListServicesSelectingPodAsync(client, contextName, namespaceName, resource, cancellationToken));
        var relatedIngressesResult = await TryLoadOptionalRelatedResourcesAsync(
            contextName,
            $"ingress relationships for pod '{name}'",
            () => ListIngressesReferencingServicesAsync(
                client,
                contextName,
                namespaceName,
                selectedServicesResult.RelatedResources.Select(static service => service.Name),
                cancellationToken));

        return WithTransparency(
            KubeResourceDetailFactory.Create(
                contextName,
                resource,
                selectedServicesResult.RelatedResources.Concat(relatedIngressesResult.RelatedResources).ToArray()) with
            {
                Warnings = selectedServicesResult.Warnings.Concat(relatedIngressesResult.Warnings).ToArray()
            },
            new KubeResourceDetailRequest
            {
                ContextName = contextName,
                Kind = KubeResourceKind.Pod,
                Namespace = namespaceName,
                Name = name
            });
    }

    private static async Task<KubeResourceDetailResponse> GetDeploymentDetailAsync(
        Kubernetes client,
        string contextName,
        string namespaceName,
        string name,
        CancellationToken cancellationToken)
    {
        var resource = await client.ReadNamespacedDeploymentAsync(name, namespaceName, cancellationToken: cancellationToken);
        var relatedPodsResult = await TryLoadOptionalRelatedResourcesAsync(
            contextName,
            $"selected pods for deployment '{name}'",
            () => ListPodsBySelectorAsync(client, contextName, namespaceName, resource.Spec?.Selector?.MatchLabels, cancellationToken));

        return WithTransparency(
            KubeResourceDetailFactory.Create(contextName, resource, relatedPodsResult.RelatedResources) with
            {
                Warnings = relatedPodsResult.Warnings
            },
            new KubeResourceDetailRequest
            {
                ContextName = contextName,
                Kind = KubeResourceKind.Deployment,
                Namespace = namespaceName,
                Name = name
            });
    }

    private static async Task<KubeResourceDetailResponse> GetReplicaSetDetailAsync(
        Kubernetes client,
        string contextName,
        string namespaceName,
        string name,
        CancellationToken cancellationToken)
    {
        var resource = await client.ReadNamespacedReplicaSetAsync(name, namespaceName, cancellationToken: cancellationToken);
        var relatedPodsResult = await TryLoadOptionalRelatedResourcesAsync(
            contextName,
            $"selected pods for ReplicaSet '{name}'",
            () => ListPodsBySelectorAsync(client, contextName, namespaceName, resource.Spec?.Selector?.MatchLabels, cancellationToken));

        return WithTransparency(
            KubeResourceDetailFactory.Create(contextName, resource, relatedPodsResult.RelatedResources) with
            {
                Warnings = relatedPodsResult.Warnings
            },
            new KubeResourceDetailRequest
            {
                ContextName = contextName,
                Kind = KubeResourceKind.ReplicaSet,
                Namespace = namespaceName,
                Name = name
            });
    }

    private static async Task<KubeResourceDetailResponse> GetStatefulSetDetailAsync(
        Kubernetes client,
        string contextName,
        string namespaceName,
        string name,
        CancellationToken cancellationToken)
    {
        var resource = await client.ReadNamespacedStatefulSetAsync(name, namespaceName, cancellationToken: cancellationToken);
        var relatedPodsResult = await TryLoadOptionalRelatedResourcesAsync(
            contextName,
            $"selected pods for StatefulSet '{name}'",
            () => ListPodsBySelectorAsync(client, contextName, namespaceName, resource.Spec?.Selector?.MatchLabels, cancellationToken));

        return WithTransparency(
            KubeResourceDetailFactory.Create(contextName, resource, relatedPodsResult.RelatedResources) with
            {
                Warnings = relatedPodsResult.Warnings
            },
            new KubeResourceDetailRequest
            {
                ContextName = contextName,
                Kind = KubeResourceKind.StatefulSet,
                Namespace = namespaceName,
                Name = name
            });
    }

    private static async Task<KubeResourceDetailResponse> GetDaemonSetDetailAsync(
        Kubernetes client,
        string contextName,
        string namespaceName,
        string name,
        CancellationToken cancellationToken)
    {
        var resource = await client.ReadNamespacedDaemonSetAsync(name, namespaceName, cancellationToken: cancellationToken);
        var relatedPodsResult = await TryLoadOptionalRelatedResourcesAsync(
            contextName,
            $"selected pods for DaemonSet '{name}'",
            () => ListPodsBySelectorAsync(client, contextName, namespaceName, resource.Spec?.Selector?.MatchLabels, cancellationToken));

        return WithTransparency(
            KubeResourceDetailFactory.Create(contextName, resource, relatedPodsResult.RelatedResources) with
            {
                Warnings = relatedPodsResult.Warnings
            },
            new KubeResourceDetailRequest
            {
                ContextName = contextName,
                Kind = KubeResourceKind.DaemonSet,
                Namespace = namespaceName,
                Name = name
            });
    }

    private static async Task<KubeResourceDetailResponse> GetServiceDetailAsync(
        Kubernetes client,
        string contextName,
        string namespaceName,
        string name,
        CancellationToken cancellationToken)
    {
        var resource = await client.ReadNamespacedServiceAsync(name, namespaceName, cancellationToken: cancellationToken);
        var relatedPodsResult = await TryLoadOptionalRelatedResourcesAsync(
            contextName,
            $"selected pods for service '{name}'",
            () => ListPodsBySelectorAsync(client, contextName, namespaceName, resource.Spec?.Selector, cancellationToken));

        return WithTransparency(
            KubeResourceDetailFactory.Create(contextName, resource, relatedPodsResult.RelatedResources) with
            {
                Warnings = relatedPodsResult.Warnings
            },
            new KubeResourceDetailRequest
            {
                ContextName = contextName,
                Kind = KubeResourceKind.Service,
                Namespace = namespaceName,
                Name = name
            });
    }

    private static async Task<KubeResourceDetailResponse> GetIngressDetailAsync(
        Kubernetes client,
        string contextName,
        string namespaceName,
        string name,
        CancellationToken cancellationToken)
    {
        var resource = await client.ReadNamespacedIngressAsync(name, namespaceName, cancellationToken: cancellationToken);
        var relatedServicesResult = await TryLoadOptionalRelatedResourcesAsync(
            contextName,
            $"backend services for ingress '{name}'",
            () => ListIngressBackendServicesAsync(client, contextName, namespaceName, resource, cancellationToken));

        return WithTransparency(
            KubeResourceDetailFactory.Create(contextName, resource, relatedServicesResult.RelatedResources) with
            {
                Warnings = relatedServicesResult.Warnings
            },
            new KubeResourceDetailRequest
            {
                ContextName = contextName,
                Kind = KubeResourceKind.Ingress,
                Namespace = namespaceName,
                Name = name
            });
    }

    private static async Task<KubeResourceDetailResponse> GetConfigMapDetailAsync(
        Kubernetes client,
        string contextName,
        string namespaceName,
        string name,
        CancellationToken cancellationToken)
    {
        var resource = await client.ReadNamespacedConfigMapAsync(name, namespaceName, cancellationToken: cancellationToken);
        return WithTransparency(
            KubeResourceDetailFactory.Create(contextName, resource),
            new KubeResourceDetailRequest
            {
                ContextName = contextName,
                Kind = KubeResourceKind.ConfigMap,
                Namespace = namespaceName,
                Name = name
            });
    }

    private static async Task<KubeResourceDetailResponse> GetSecretDetailAsync(
        Kubernetes client,
        string contextName,
        string namespaceName,
        string name,
        CancellationToken cancellationToken)
    {
        var resource = await client.ReadNamespacedSecretAsync(name, namespaceName, cancellationToken: cancellationToken);
        return WithTransparency(
            KubeResourceDetailFactory.Create(contextName, resource),
            new KubeResourceDetailRequest
            {
                ContextName = contextName,
                Kind = KubeResourceKind.Secret,
                Namespace = namespaceName,
                Name = name
            });
    }

    private static async Task<KubeResourceDetailResponse> GetJobDetailAsync(
        Kubernetes client,
        string contextName,
        string namespaceName,
        string name,
        CancellationToken cancellationToken)
    {
        var resource = await client.ReadNamespacedJobAsync(name, namespaceName, cancellationToken: cancellationToken);
        var relatedPodsResult = await TryLoadOptionalRelatedResourcesAsync(
            contextName,
            $"pods for Job '{name}'",
            () => ListPodsBySelectorAsync(
                client,
                contextName,
                namespaceName,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["job-name"] = name },
                cancellationToken));

        return WithTransparency(
            KubeResourceDetailFactory.Create(contextName, resource, relatedPodsResult.RelatedResources) with
            {
                Warnings = relatedPodsResult.Warnings
            },
            new KubeResourceDetailRequest
            {
                ContextName = contextName,
                Kind = KubeResourceKind.Job,
                Namespace = namespaceName,
                Name = name
            });
    }

    private static async Task<KubeResourceDetailResponse> GetCronJobDetailAsync(
        Kubernetes client,
        string contextName,
        string namespaceName,
        string name,
        CancellationToken cancellationToken)
    {
        var resource = await client.ReadNamespacedCronJobAsync(name, namespaceName, cancellationToken: cancellationToken);
        var relatedJobsResult = await TryLoadOptionalRelatedResourcesAsync(
            contextName,
            $"spawned jobs for CronJob '{name}'",
            async () =>
            {
                var jobs = await client.ListNamespacedJobAsync(namespaceName, cancellationToken: cancellationToken);
                return jobs.Items
                    .Where(job => job.Metadata?.OwnerReferences?.Any(owner =>
                        string.Equals(owner.Kind, "CronJob", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(owner.Name, name, StringComparison.Ordinal)) is true)
                    .Take(RelatedResourceLimit)
                    .Select(job => ToRelatedResource("Spawned job", KubeResourceSummaryFactory.Create(contextName, job)))
                    .ToArray();
            });

        return WithTransparency(
            KubeResourceDetailFactory.Create(contextName, resource, relatedJobsResult.RelatedResources) with
            {
                Warnings = relatedJobsResult.Warnings
            },
            new KubeResourceDetailRequest
            {
                ContextName = contextName,
                Kind = KubeResourceKind.CronJob,
                Namespace = namespaceName,
                Name = name
            });
    }

    private static async Task<KubeResourceDetailResponse> GetEventDetailAsync(
        Kubernetes client,
        string contextName,
        string namespaceName,
        string name,
        CancellationToken cancellationToken)
    {
        var coreOperations = (ICoreV1Operations)client;
        var resource = await k8s.CoreV1OperationsExtensions.ReadNamespacedEventAsync(
            coreOperations,
            name,
            namespaceName,
            cancellationToken: cancellationToken);

        return WithTransparency(
            KubeResourceDetailFactory.Create(contextName, resource),
            new KubeResourceDetailRequest
            {
                ContextName = contextName,
                Kind = KubeResourceKind.Event,
                Namespace = namespaceName,
                Name = name
            });
    }

    private static async Task<IReadOnlyList<KubeRelatedResource>> ListPodsForNodeAsync(
        Kubernetes client,
        string contextName,
        string nodeName,
        CancellationToken cancellationToken)
    {
        var list = await client.ListPodForAllNamespacesAsync(
            fieldSelector: $"spec.nodeName={nodeName}",
            limit: RelatedResourceLimit,
            cancellationToken: cancellationToken);

        return list.Items
            .Select(item => ToRelatedResource("Scheduled pod", KubeResourceSummaryFactory.Create(contextName, item)))
            .ToArray();
    }

    private static async Task<IReadOnlyList<KubeRelatedResource>> ListPodsBySelectorAsync(
        Kubernetes client,
        string contextName,
        string namespaceName,
        IEnumerable<KeyValuePair<string, string>>? selector,
        CancellationToken cancellationToken)
    {
        var labelSelector = CreateLabelSelector(selector);

        if (string.IsNullOrWhiteSpace(labelSelector))
        {
            return [];
        }

        var list = await client.ListNamespacedPodAsync(
            namespaceName,
            labelSelector: labelSelector,
            limit: RelatedResourceLimit,
            cancellationToken: cancellationToken);

        return list.Items
            .Select(item => ToRelatedResource("Selected pod", KubeResourceSummaryFactory.Create(contextName, item)))
            .ToArray();
    }

    private static async Task<IReadOnlyList<KubeRelatedResource>> ListServicesSelectingPodAsync(
        Kubernetes client,
        string contextName,
        string namespaceName,
        V1Pod pod,
        CancellationToken cancellationToken)
    {
        if (pod.Metadata?.Labels is null || pod.Metadata.Labels.Count is 0)
        {
            return [];
        }

        var list = await client.ListNamespacedServiceAsync(
            namespaceName,
            limit: RelatedResourceLimit,
            cancellationToken: cancellationToken);

        return list.Items
            .Where(service => ServiceSelectsPod(service, pod.Metadata.Labels))
            .Select(service => ToRelatedResource("Selected by service", KubeResourceSummaryFactory.Create(contextName, service)))
            .ToArray();
    }

    private static async Task<IReadOnlyList<KubeRelatedResource>> ListIngressesReferencingServicesAsync(
        Kubernetes client,
        string contextName,
        string namespaceName,
        IEnumerable<string> serviceNames,
        CancellationToken cancellationToken)
    {
        var serviceNameSet = serviceNames
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.Ordinal);

        if (serviceNameSet.Count is 0)
        {
            return [];
        }

        var ingressList = await client.ListNamespacedIngressAsync(
            namespaceName,
            limit: RelatedResourceLimit,
            cancellationToken: cancellationToken);

        return ingressList.Items
            .Where(ingress => IngressReferencesAnyService(ingress, serviceNameSet))
            .Select(ingress => ToRelatedResource("Routed by ingress", KubeResourceSummaryFactory.Create(contextName, ingress)))
            .ToArray();
    }

    private static async Task<IReadOnlyList<KubeRelatedResource>> ListIngressBackendServicesAsync(
        Kubernetes client,
        string contextName,
        string namespaceName,
        V1Ingress ingress,
        CancellationToken cancellationToken)
    {
        var serviceNames = new HashSet<string>(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(ingress.Spec?.DefaultBackend?.Service?.Name))
        {
            serviceNames.Add(ingress.Spec.DefaultBackend.Service.Name);
        }

        foreach (var rule in ingress.Spec?.Rules ?? [])
        {
            foreach (var path in rule.Http?.Paths ?? [])
            {
                var serviceName = path.Backend?.Service?.Name;

                if (!string.IsNullOrWhiteSpace(serviceName))
                {
                    serviceNames.Add(serviceName);
                }
            }
        }

        if (serviceNames.Count is 0)
        {
            return [];
        }

        var relatedServices = new List<KubeRelatedResource>(serviceNames.Count);

        foreach (var serviceName in serviceNames)
        {
            var service = await client.ReadNamespacedServiceAsync(serviceName, namespaceName, cancellationToken: cancellationToken);
            relatedServices.Add(ToRelatedResource("Backend service", KubeResourceSummaryFactory.Create(contextName, service)));
        }

        return relatedServices;
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

        return parts.Length is 0
            ? null
            : string.Join(",", parts);
    }

    private static bool ServiceSelectsPod(V1Service service, IDictionary<string, string> podLabels)
    {
        var selector = service.Spec?.Selector;

        return selector is not null &&
               selector.Count > 0 &&
               selector.All(pair => podLabels.TryGetValue(pair.Key, out var value) && string.Equals(value, pair.Value, StringComparison.Ordinal));
    }

    private static bool IngressReferencesAnyService(V1Ingress ingress, ISet<string> serviceNames)
    {
        if (!string.IsNullOrWhiteSpace(ingress.Spec?.DefaultBackend?.Service?.Name) &&
            serviceNames.Contains(ingress.Spec.DefaultBackend.Service.Name))
        {
            return true;
        }

        foreach (var rule in ingress.Spec?.Rules ?? [])
        {
            foreach (var path in rule.Http?.Paths ?? [])
            {
                if (!string.IsNullOrWhiteSpace(path.Backend?.Service?.Name) &&
                    serviceNames.Contains(path.Backend.Service.Name))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static KubeRelatedResource ToRelatedResource(string relationship, KubeResourceSummary summary)
    {
        return new KubeRelatedResource(
            Relationship: relationship,
            Kind: summary.Kind,
            ApiVersion: summary.ApiVersion,
            Name: summary.Name,
            Namespace: summary.Namespace,
            Status: summary.Status,
            Summary: summary.Summary);
    }

    internal static async Task<OptionalRelatedResourcesResult> TryLoadOptionalRelatedResourcesAsync(
        string contextName,
        string scopeSummary,
        Func<Task<IReadOnlyList<KubeRelatedResource>>> query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contextName);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeSummary);
        ArgumentNullException.ThrowIfNull(query);

        try
        {
            return new OptionalRelatedResourcesResult(await query(), []);
        }
        catch (HttpOperationException exception) when (exception.Response.StatusCode is HttpStatusCode.Forbidden)
        {
            return new OptionalRelatedResourcesResult(
                [],
                [new KubeQueryWarning(
                    contextName,
                    $"Skipped {scopeSummary} because the current Kubernetes identity is not allowed to read that related resource set. {exception.Message}")]);
        }
        catch (HttpOperationException exception) when (exception.Response.StatusCode is HttpStatusCode.NotFound)
        {
            return new OptionalRelatedResourcesResult(
                [],
                [new KubeQueryWarning(
                    contextName,
                    $"Skipped {scopeSummary} because the related resources changed before inspection completed. {exception.Message}")]);
        }
    }

    private static KubeResourceDetailResponse WithTransparency(
        KubeResourceDetailResponse response,
        KubeResourceDetailRequest request)
    {
        return response with
        {
            TransparencyCommands = KubectlTransparencyFactory.CreateForDetail(request)
        };
    }
}

internal sealed record OptionalRelatedResourcesResult(
    IReadOnlyList<KubeRelatedResource> RelatedResources,
    IReadOnlyList<KubeQueryWarning> Warnings);
