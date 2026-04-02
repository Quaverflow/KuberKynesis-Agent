using Kuberkynesis.Agent.Kube;
using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Tests;

public sealed class KubeResourceCauseInferenceTests
{
    [Fact]
    public void InferLikelyCauses_EmitsExpectedCauseDescriptors()
    {
        var causes = KubeResourceCauseInference.InferLikelyCauses(
        [
            new KubeResourceTimelineEvent(
                OccurredAtUtc: new DateTimeOffset(2026, 4, 1, 9, 0, 0, TimeSpan.Zero),
                SourceRelationship: "Selected resource",
                SourceKind: KubeResourceKind.Pod,
                SourceName: "orders-api-abc123",
                SourceNamespace: "orders-prod",
                Type: "Warning",
                Reason: "Unhealthy",
                Message: "Readiness probe failed: HTTP probe failed",
                Count: 2,
                IsRootResource: true),
            new KubeResourceTimelineEvent(
                OccurredAtUtc: new DateTimeOffset(2026, 4, 1, 8, 58, 0, TimeSpan.Zero),
                SourceRelationship: "Owned by",
                SourceKind: KubeResourceKind.ReplicaSet,
                SourceName: "orders-api-5d4566bdf6",
                SourceNamespace: "orders-prod",
                Type: "Normal",
                Reason: "ScalingReplicaSet",
                Message: "Scaled up replica set orders-api-5d4566bdf6 to 1",
                Count: 1,
                IsRootResource: false)
        ]);

        Assert.Contains(causes, cause => cause.EventType == "Health issue" && cause.Severity == "warning");
        Assert.DoesNotContain(causes, cause => cause.EventType == "Rollout activity");
    }

    [Fact]
    public void InferLikelyCauses_EmitsRolloutActivityForRootResourceEvents()
    {
        var causes = KubeResourceCauseInference.InferLikelyCauses(
        [
            new KubeResourceTimelineEvent(
                OccurredAtUtc: new DateTimeOffset(2026, 4, 1, 9, 5, 0, TimeSpan.Zero),
                SourceRelationship: "Selected resource",
                SourceKind: KubeResourceKind.Deployment,
                SourceName: "orders-api",
                SourceNamespace: "orders-prod",
                Type: "Normal",
                Reason: "ScalingReplicaSet",
                Message: "Scaled up replica set orders-api-5d4566bdf6 to 1",
                Count: 1,
                IsRootResource: true)
        ]);

        var cause = Assert.Single(causes);
        Assert.Equal("Rollout activity", cause.EventType);
        Assert.Equal("normal", cause.Severity);
    }
}
