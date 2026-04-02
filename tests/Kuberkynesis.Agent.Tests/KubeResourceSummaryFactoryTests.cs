using k8s.Models;
using Kuberkynesis.Agent.Kube;
using Kuberkynesis.Ui.Shared.Kubernetes;
using System.Text.Json.Nodes;

namespace Kuberkynesis.Agent.Tests;

public sealed class KubeResourceSummaryFactoryTests
{
    [Fact]
    public void Create_PodSummary_UsesReadinessCounts()
    {
        var pod = new V1Pod
        {
            ApiVersion = "v1",
            Metadata = new V1ObjectMeta
            {
                Name = "checkout-api-6f8c",
                NamespaceProperty = "storefront",
                Uid = "uid-123"
            },
            Spec = new V1PodSpec
            {
                Containers =
                [
                    new V1Container { Name = "app" },
                    new V1Container { Name = "metrics" }
                ]
            },
            Status = new V1PodStatus
            {
                Phase = "Running",
                ContainerStatuses =
                [
                    new V1ContainerStatus
                    {
                        Name = "app",
                        Image = "app:v1",
                        ImageID = "sha256:1",
                        Ready = true,
                        RestartCount = 0
                    },
                    new V1ContainerStatus
                    {
                        Name = "metrics",
                        Image = "metrics:v1",
                        ImageID = "sha256:2",
                        Ready = false,
                        RestartCount = 0
                    }
                ]
            }
        };

        var summary = KubeResourceSummaryFactory.Create("dev-eu", pod);

        Assert.Equal("dev-eu", summary.ContextName);
        Assert.Equal("checkout-api-6f8c", summary.Name);
        Assert.Equal("storefront", summary.Namespace);
        Assert.Equal("Running", summary.Status);
        Assert.Equal(1, summary.ReadyReplicas);
        Assert.Equal(2, summary.DesiredReplicas);
        Assert.Equal("1/2 ready", summary.Summary);
    }

    [Fact]
    public void Create_SecretSummary_DoesNotExposeValues()
    {
        var secret = new V1Secret
        {
            ApiVersion = "v1",
            Metadata = new V1ObjectMeta
            {
                Name = "payments-api",
                NamespaceProperty = "payments"
            },
            Type = "Opaque",
            Data = new Dictionary<string, byte[]>
            {
                ["client-id"] = [1, 2, 3],
                ["client-secret"] = [4, 5, 6]
            }
        };

        var summary = KubeResourceSummaryFactory.Create("prod-eu", secret);

        Assert.Equal("Present", summary.Status);
        Assert.Equal("Opaque | 2 keys", summary.Summary);
        Assert.DoesNotContain("client-secret", summary.Summary ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_ReplicaSetSummary_UsesReplicaReadiness()
    {
        var replicaSet = new V1ReplicaSet
        {
            ApiVersion = "apps/v1",
            Metadata = new V1ObjectMeta
            {
                Name = "orders-api-6c57d5cf6f",
                NamespaceProperty = "orders-prod"
            },
            Spec = new V1ReplicaSetSpec
            {
                Replicas = 3
            },
            Status = new V1ReplicaSetStatus
            {
                ReadyReplicas = 2
            }
        };

        var summary = KubeResourceSummaryFactory.Create("kind-kuberkynesis-lab", replicaSet);

        Assert.Equal(KubeResourceKind.ReplicaSet, summary.Kind);
        Assert.Equal("Progressing", summary.Status);
        Assert.Equal(2, summary.ReadyReplicas);
        Assert.Equal(3, summary.DesiredReplicas);
        Assert.Equal("2/3 ready", summary.Summary);
    }

    [Fact]
    public void Create_EventSummary_UsesTypeReasonAndMessageWithoutDumpingEverything()
    {
        var item = new Corev1Event
        {
            ApiVersion = "v1",
            Metadata = new V1ObjectMeta
            {
                Name = "orders-api-abc123.182f8d4c0f9ad6e1",
                NamespaceProperty = "orders-prod"
            },
            Type = "Warning",
            Reason = "Unhealthy",
            Message = "Readiness probe failed: Get http://10.244.0.14:8080/healthz: context deadline exceeded while the upstream service was warming up after a rollout and the kubelet kept retrying the same endpoint without success",
            Count = 3
        };

        var summary = KubeResourceSummaryFactory.Create("kind-kuberkynesis-lab", item);

        Assert.Equal(KubeResourceKind.Event, summary.Kind);
        Assert.Equal("Warning", summary.Status);
        Assert.Contains("x3 | Unhealthy", summary.Summary, StringComparison.Ordinal);
        Assert.Contains("Readiness probe failed", summary.Summary, StringComparison.Ordinal);
        Assert.True(summary.Summary!.Length <= 120);
        Assert.EndsWith("...", summary.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void MatchesSearch_IncludesStatusText()
    {
        var item = new Corev1Event
        {
            ApiVersion = "v1",
            Metadata = new V1ObjectMeta
            {
                Name = "orders-api-abc123.182f8d4c0f9ad6e1",
                NamespaceProperty = "orders-prod"
            },
            Type = "Warning",
            Reason = "Unhealthy",
            Message = "Readiness probe failed"
        };

        var summary = KubeResourceSummaryFactory.Create("kind-kuberkynesis-lab", item);

        Assert.True(KubeResourceSummaryFactory.MatchesSearch(summary, "warning"));
    }

    [Fact]
    public void MatchesSearch_SupportsAnyOfMultiplePipeSeparatedTerms()
    {
        var pod = new V1Pod
        {
            ApiVersion = "v1",
            Metadata = new V1ObjectMeta
            {
                Name = "checkout-api-0",
                NamespaceProperty = "checkout-prod"
            },
            Status = new V1PodStatus
            {
                Phase = "Running"
            }
        };

        var summary = KubeResourceSummaryFactory.Create("kind-kuberkynesis-lab", pod);

        Assert.True(KubeResourceSummaryFactory.MatchesSearch(summary, "orders-api | checkout-api"));
        Assert.False(KubeResourceSummaryFactory.MatchesSearch(summary, "orders-api | billing-api"));
    }

    [Fact]
    public void Create_CustomResourceSummary_UsesStatusPhaseAndCarriesTheCustomType()
    {
        var customResourceType = new KubeCustomResourceType(
            Group: "stable.example.io",
            Version: "v1",
            Kind: "Widget",
            Plural: "widgets",
            Namespaced: true);
        var item = new JsonObject
        {
            ["apiVersion"] = customResourceType.ApiVersion,
            ["kind"] = customResourceType.Kind,
            ["metadata"] = new JsonObject
            {
                ["name"] = "checkout-widget",
                ["namespace"] = "checkout-prod",
                ["uid"] = "widget-uid-01"
            },
            ["status"] = new JsonObject
            {
                ["phase"] = "Ready"
            }
        };

        var summary = KubeResourceSummaryFactory.Create("kind-kuberkynesis-lab", customResourceType, item);

        Assert.Equal(KubeResourceKind.CustomResource, summary.Kind);
        Assert.Equal("checkout-widget", summary.Name);
        Assert.Equal("checkout-prod", summary.Namespace);
        Assert.Equal("Ready", summary.Status);
        Assert.Equal("Phase Ready", summary.Summary);
        Assert.Equal("stable.example.io/v1/widgets", summary.CustomResourceType?.DefinitionId);
        Assert.True(KubeResourceSummaryFactory.MatchesSearch(summary, "widget"));
    }
}
