using k8s.Models;
using Kuberkynesis.Agent.Kube;

namespace Kuberkynesis.Agent.Tests;

public sealed class KubeDeploymentRollbackPlannerTests
{
    [Fact]
    public void Resolve_WithRetainedHistory_PicksThePreviousRevision()
    {
        var deployment = CreateDeployment("orders-api", "orders-prod", revision: "9");
        var resolution = KubeDeploymentRollbackPlanner.Resolve(
            deployment,
            [
                CreateReplicaSet("orders-api-6f4d9b4c8d", "orders-api", "orders-prod", revision: "9", image: "orders-api:v3", changeCause: "deploy v3"),
                CreateReplicaSet("orders-api-74cc9f49f4", "orders-api", "orders-prod", revision: "8", image: "orders-api:v2", changeCause: "deploy v2"),
                CreateReplicaSet("orders-api-5d4566bdf6", "orders-api", "orders-prod", revision: "7", image: "orders-api:v1", changeCause: "deploy v1")
            ]);

        Assert.True(resolution.CanRollback);
        Assert.Equal(9, resolution.CurrentRevision);
        Assert.Equal(8, resolution.PreviousRevision);
        Assert.Equal(3, resolution.RetainedRevisionCount);
        Assert.False(resolution.UsedReplicaSetRevisionFallback);
        Assert.Equal("deploy v2", resolution.PreviousChangeCause);
        Assert.Equal("orders-api-74cc9f49f4", resolution.PreviousReplicaSet!.Metadata!.Name);
        Assert.Equal("app=orders-api:v2", KubeDeploymentRollbackPlanner.GetTemplateImageSummary(resolution.PreviousReplicaSet.Spec!.Template));
    }

    [Fact]
    public void Resolve_WithoutDeploymentRevision_FallsBackToTheNewestReplicaSet()
    {
        var deployment = CreateDeployment("orders-api", "orders-prod", revision: null);
        var resolution = KubeDeploymentRollbackPlanner.Resolve(
            deployment,
            [
                CreateReplicaSet("orders-api-74cc9f49f4", "orders-api", "orders-prod", revision: "11", image: "orders-api:v3"),
                CreateReplicaSet("orders-api-5d4566bdf6", "orders-api", "orders-prod", revision: "10", image: "orders-api:v2")
            ]);

        Assert.True(resolution.CanRollback);
        Assert.Equal(11, resolution.CurrentRevision);
        Assert.Equal(10, resolution.PreviousRevision);
        Assert.True(resolution.UsedReplicaSetRevisionFallback);
    }

    [Fact]
    public void Resolve_WithOnlyOneVisibleRevision_CannotRollback()
    {
        var deployment = CreateDeployment("orders-api", "orders-prod", revision: "4");
        var resolution = KubeDeploymentRollbackPlanner.Resolve(
            deployment,
            [
                CreateReplicaSet("orders-api-74cc9f49f4", "orders-api", "orders-prod", revision: "4", image: "orders-api:v4")
            ]);

        Assert.False(resolution.CanRollback);
        Assert.Equal(4, resolution.CurrentRevision);
        Assert.Null(resolution.PreviousRevision);
        Assert.Null(resolution.PreviousReplicaSet);
        Assert.Equal(1, resolution.RetainedRevisionCount);
    }

    private static V1Deployment CreateDeployment(string name, string namespaceName, string? revision)
    {
        var annotations = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(revision))
        {
            annotations["deployment.kubernetes.io/revision"] = revision;
        }

        return new V1Deployment
        {
            Metadata = new V1ObjectMeta
            {
                Name = name,
                NamespaceProperty = namespaceName,
                Annotations = annotations
            },
            Spec = new V1DeploymentSpec
            {
                Selector = new V1LabelSelector
                {
                    MatchLabels = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["app"] = name
                    }
                },
                Template = new V1PodTemplateSpec
                {
                    Spec = new V1PodSpec
                    {
                        Containers =
                        [
                            new V1Container
                            {
                                Name = "app",
                                Image = "orders-api:current"
                            }
                        ]
                    }
                }
            }
        };
    }

    private static V1ReplicaSet CreateReplicaSet(
        string name,
        string deploymentName,
        string namespaceName,
        string revision,
        string image,
        string? changeCause = null)
    {
        var annotations = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["deployment.kubernetes.io/revision"] = revision
        };

        if (!string.IsNullOrWhiteSpace(changeCause))
        {
            annotations["kubernetes.io/change-cause"] = changeCause;
        }

        return new V1ReplicaSet
        {
            ApiVersion = "apps/v1",
            Metadata = new V1ObjectMeta
            {
                Name = name,
                NamespaceProperty = namespaceName,
                CreationTimestamp = DateTime.Parse($"2026-03-{20 + int.Parse(revision):00}T08:00:00Z"),
                Annotations = annotations,
                OwnerReferences =
                [
                    new V1OwnerReference
                    {
                        ApiVersion = "apps/v1",
                        Kind = "Deployment",
                        Name = deploymentName,
                        Uid = "uid-1",
                        Controller = true
                    }
                ]
            },
            Spec = new V1ReplicaSetSpec
            {
                Template = new V1PodTemplateSpec
                {
                    Spec = new V1PodSpec
                    {
                        Containers =
                        [
                            new V1Container
                            {
                                Name = "app",
                                Image = image
                            }
                        ]
                    }
                }
            }
        };
    }
}
