using Kuberkynesis.Agent.Kube;
using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Tests;

public sealed class KubeResourceTimelineFactoryTests
{
    [Fact]
    public void Create_SortsNewestEventsFirstAndInfersLikelyCauses()
    {
        var resource = new KubeResourceSummary(
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
            Labels: new Dictionary<string, string>());

        var timeline = KubeResourceTimelineFactory.Create(
            resource,
            [
                new KubeResourceTimelineEvent(
                    OccurredAtUtc: new DateTimeOffset(2026, 3, 29, 10, 15, 0, TimeSpan.Zero),
                    SourceRelationship: "Selected resource",
                    SourceKind: KubeResourceKind.Pod,
                    SourceName: "orders-api-abc123",
                    SourceNamespace: "orders-prod",
                    Type: "Warning",
                    Reason: "Unhealthy",
                    Message: "Readiness probe failed: HTTP probe failed",
                    Count: 3,
                    IsRootResource: true),
                new KubeResourceTimelineEvent(
                    OccurredAtUtc: new DateTimeOffset(2026, 3, 29, 10, 10, 0, TimeSpan.Zero),
                    SourceRelationship: "Owned by",
                    SourceKind: KubeResourceKind.ReplicaSet,
                    SourceName: "orders-api-5d4566bdf6",
                    SourceNamespace: "orders-prod",
                    Type: "Normal",
                    Reason: "ScalingReplicaSet",
                    Message: "Scaled up replica set orders-api-5d4566bdf6 to 1",
                    Count: 1,
                    IsRootResource: false)
            ],
            [
                new KubectlCommandPreview(
                    Label: "Selected resource events",
                    Command: "kubectl --context kind-kuberkynesis-lab -n orders-prod get events --field-selector involvedObject.kind=Pod,involvedObject.name=orders-api-abc123 --sort-by=.lastTimestamp")
            ]);

        Assert.Equal("Unhealthy", timeline.Events[0].Reason);
        Assert.Contains(timeline.LikelyCauses, cause => cause.Contains("probe health issue", StringComparison.OrdinalIgnoreCase));
        Assert.Single(timeline.TransparencyCommands!);
    }
}
