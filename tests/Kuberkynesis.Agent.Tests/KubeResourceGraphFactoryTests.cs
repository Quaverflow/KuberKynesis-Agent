using Kuberkynesis.Agent.Kube;
using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Tests;

public sealed class KubeResourceGraphFactoryTests
{
    [Fact]
    public void Create_BuildsUpstreamAndDownstreamEdgesAroundTheRootResource()
    {
        var detail = new KubeResourceDetailResponse(
            Resource: new KubeResourceSummary(
                ContextName: "kind-kuberkynesis-lab",
                Kind: KubeResourceKind.Pod,
                ApiVersion: "v1",
                Name: "orders-api-abc123",
                Namespace: "orders-prod",
                Uid: "pod-01",
                Status: "Running",
                Summary: "1/1 ready",
                ReadyReplicas: null,
                DesiredReplicas: null,
                CreatedAtUtc: null,
                Labels: new Dictionary<string, string>()),
            Sections: [],
            RelatedResources:
            [
                new KubeRelatedResource(
                    Relationship: "Owned by",
                    Kind: KubeResourceKind.ReplicaSet,
                    ApiVersion: "apps/v1",
                    Name: "orders-api-5d4566bdf6",
                    Namespace: "orders-prod",
                    Status: "controller",
                    Summary: null),
                new KubeRelatedResource(
                    Relationship: "Scheduled on",
                    Kind: KubeResourceKind.Node,
                    ApiVersion: "v1",
                    Name: "worker-a",
                    Namespace: null,
                    Status: null,
                    Summary: "172.18.0.2")
            ],
            Warnings: []);

        var graph = KubeResourceGraphFactory.Create(
            detail,
            transparencyCommands:
            [
                new KubectlCommandPreview(
                    Label: "Graph seed resource",
                    Command: "kubectl --context kind-kuberkynesis-lab -n orders-prod describe pods orders-api-abc123")
            ]);

        Assert.Equal(graph.RootNodeId, Assert.Single(graph.Nodes, node => node.IsRoot).Id);
        Assert.Contains(graph.Nodes, node => node.Kind == KubeResourceKind.ReplicaSet && node.Name == "orders-api-5d4566bdf6");
        Assert.Contains(graph.Nodes, node => node.Kind == KubeResourceKind.Node && node.Name == "worker-a");

        Assert.Contains(graph.Edges, edge =>
            edge.Relationship == "Owned by" &&
            edge.ToNodeId == graph.RootNodeId);

        Assert.Contains(graph.Edges, edge =>
            edge.Relationship == "Scheduled on" &&
            edge.FromNodeId == graph.RootNodeId);

        Assert.Single(graph.TransparencyCommands!);
    }
}
