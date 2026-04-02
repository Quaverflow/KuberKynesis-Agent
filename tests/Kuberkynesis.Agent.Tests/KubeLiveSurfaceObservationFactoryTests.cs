using Kuberkynesis.Agent.Kube;
using Kuberkynesis.LiveSurface;
using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Tests;

public sealed class KubeLiveSurfaceObservationFactoryTests
{
    [Fact]
    public void CreateDerivedEnvelopes_AddsStatusActivityAndCauseObservations()
    {
        var resource = new KubeResourceSummary(
            ContextName: "kind-kuberkynesis-lab",
            Kind: KubeResourceKind.Pod,
            ApiVersion: "v1",
            Name: "orders-api-abc123",
            Namespace: "orders-prod",
            Uid: "pod-01",
            Status: "Pending",
            Summary: "0/1 ready",
            ReadyReplicas: 0,
            DesiredReplicas: 1,
            CreatedAtUtc: null,
            Labels: new Dictionary<string, string>());

        var derived = KubeLiveSurfaceObservationFactory.CreateDerivedEnvelopes(
            resource,
            [
                CreateEventEnvelope(
                    eventType: "Unhealthy",
                    severity: "warning",
                    summary: "Readiness probe failed"),
                CreateEventEnvelope(
                    eventType: "FailedScheduling",
                    severity: "warning",
                    summary: "0/3 nodes are available: Insufficient cpu.",
                    reason: "FailedScheduling")
            ],
            observedAtUtc: new DateTimeOffset(2026, 4, 1, 10, 0, 0, TimeSpan.Zero));

        Assert.Contains(derived, envelope => envelope.Category == "status" && envelope.EventType == "Status summary" && envelope.Severity == "warning");
        Assert.Contains(derived, envelope => envelope.Category == "activity" && envelope.EventType == "Warning activity");
        Assert.Contains(derived, envelope => envelope.Category == "cause" && envelope.EventType == "Scheduling pressure");
        Assert.All(derived, envelope => Assert.Equal("kuberkynesis.observations", envelope.Stream));
    }

    [Fact]
    public void CreateDerivedEnvelopes_StillAddsStatusWhenNoRawEventsExist()
    {
        var resource = new KubeResourceSummary(
            ContextName: "kind-kuberkynesis-lab",
            Kind: KubeResourceKind.Deployment,
            ApiVersion: "apps/v1",
            Name: "orders-api",
            Namespace: "orders-prod",
            Uid: "deploy-01",
            Status: "Running",
            Summary: "3/3 ready",
            ReadyReplicas: 3,
            DesiredReplicas: 3,
            CreatedAtUtc: null,
            Labels: new Dictionary<string, string>());

        var derived = KubeLiveSurfaceObservationFactory.CreateDerivedEnvelopes(
            resource,
            [],
            observedAtUtc: new DateTimeOffset(2026, 4, 1, 10, 0, 0, TimeSpan.Zero));

        var envelope = Assert.Single(derived);
        Assert.Equal("status", envelope.Category);
        Assert.Equal("Status summary", envelope.EventType);
        Assert.Equal("normal", envelope.Severity);
    }

    private static LiveSurfaceEnvelope CreateEventEnvelope(
        string eventType,
        string severity,
        string summary,
        string relationship = "Selected resource",
        string reason = "")
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["relationship"] = relationship,
            ["count"] = "1"
        };

        if (!string.IsNullOrWhiteSpace(reason))
        {
            fields["reason"] = reason;
        }

        return new LiveSurfaceEnvelope(
            SchemaVersion: "kubernetes.event.v1",
            Stream: "kubernetes.events",
            EventType: eventType,
            TimestampUtc: new DateTimeOffset(2026, 4, 1, 9, 55, 0, TimeSpan.Zero),
            Severity: severity,
            Summary: summary,
            Namespace: "orders-prod",
            ResourceKind: "Pod",
            ResourceName: "orders-api-abc123",
            Component: "kubelet",
            Tags: new Dictionary<string, string>(),
            Fields: fields,
            Category: "event");
    }
}
