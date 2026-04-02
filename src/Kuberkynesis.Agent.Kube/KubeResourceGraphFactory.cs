using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Kube;

internal static class KubeResourceGraphFactory
{
    public static KubeResourceGraphResponse Create(
        KubeResourceDetailResponse detail,
        IReadOnlyList<KubectlCommandPreview> transparencyCommands)
    {
        ArgumentNullException.ThrowIfNull(detail);

        var rootNode = CreateRootNode(detail.Resource);
        var nodes = new Dictionary<string, KubeResourceGraphNode>(StringComparer.OrdinalIgnoreCase)
        {
            [rootNode.Id] = rootNode
        };
        var edges = new List<KubeResourceGraphEdge>();

        foreach (var relatedResource in detail.RelatedResources)
        {
            var relatedNode = CreateRelatedNode(detail.Resource.ContextName, relatedResource);
            nodes.TryAdd(relatedNode.Id, relatedNode);

            var edge = CreateEdge(rootNode.Id, relatedNode.Id, relatedResource.Relationship);

            if (!edges.Any(existing =>
                    string.Equals(existing.FromNodeId, edge.FromNodeId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.ToNodeId, edge.ToNodeId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.Relationship, edge.Relationship, StringComparison.OrdinalIgnoreCase)))
            {
                edges.Add(edge);
            }
        }

        return new KubeResourceGraphResponse(
            RootNodeId: rootNode.Id,
            Nodes: nodes.Values.ToArray(),
            Edges: edges,
            Warnings: detail.Warnings,
            TransparencyCommands: transparencyCommands);
    }

    private static KubeResourceGraphNode CreateRootNode(KubeResourceSummary resource)
    {
        return new KubeResourceGraphNode(
            Id: CreateNodeId(resource.ContextName, resource.Kind, resource.Namespace, resource.Name),
            ContextName: resource.ContextName,
            Kind: resource.Kind,
            ApiVersion: resource.ApiVersion,
            Name: resource.Name,
            Namespace: resource.Namespace,
            Status: resource.Status,
            Summary: resource.Summary,
            IsRoot: true);
    }

    private static KubeResourceGraphNode CreateRelatedNode(string contextName, KubeRelatedResource relatedResource)
    {
        return new KubeResourceGraphNode(
            Id: CreateNodeId(contextName, relatedResource.Kind, relatedResource.Namespace, relatedResource.Name),
            ContextName: contextName,
            Kind: relatedResource.Kind,
            ApiVersion: relatedResource.ApiVersion,
            Name: relatedResource.Name,
            Namespace: relatedResource.Namespace,
            Status: relatedResource.Status,
            Summary: relatedResource.Summary,
            IsRoot: false);
    }

    private static KubeResourceGraphEdge CreateEdge(string rootNodeId, string relatedNodeId, string relationship)
    {
        return IsIncomingRelationship(relationship)
            ? new KubeResourceGraphEdge(relatedNodeId, rootNodeId, relationship)
            : new KubeResourceGraphEdge(rootNodeId, relatedNodeId, relationship);
    }

    private static bool IsIncomingRelationship(string relationship)
    {
        return string.Equals(relationship, "Owned by", StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateNodeId(
        string contextName,
        KubeResourceKind? kind,
        string? namespaceName,
        string name)
    {
        return KubeResourceIdentity.Create(contextName, kind, namespaceName, name);
    }
}
