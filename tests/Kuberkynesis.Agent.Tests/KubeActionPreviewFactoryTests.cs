using k8s.Models;
using Kuberkynesis.Agent.Kube;
using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Tests;

public sealed class KubeActionPreviewFactoryTests
{
    [Fact]
    public void CreateScaleDeploymentPreview_BuildsFactsWarningsAndTransparency()
    {
        var deployment = new V1Deployment
        {
            ApiVersion = "apps/v1",
            Metadata = new V1ObjectMeta
            {
                Name = "orders-api",
                NamespaceProperty = "orders-prod"
            },
            Spec = new V1DeploymentSpec
            {
                Replicas = 3,
                Paused = true,
                Selector = new V1LabelSelector
                {
                    MatchLabels = new Dictionary<string, string>
                    {
                        ["app"] = "orders-api"
                    }
                },
                Strategy = new V1DeploymentStrategy
                {
                    Type = "RollingUpdate"
                }
            },
            Status = new V1DeploymentStatus
            {
                ReadyReplicas = 2,
                AvailableReplicas = 2,
                UpdatedReplicas = 3
            }
        };

        V1Pod[] pods =
        [
            new V1Pod
            {
                ApiVersion = "v1",
                Metadata = new V1ObjectMeta
                {
                    Name = "orders-api-abc123",
                    NamespaceProperty = "orders-prod"
                },
                Status = new V1PodStatus
                {
                    Phase = "Running",
                    PodIP = "10.244.0.15"
                }
            },
            new V1Pod
            {
                ApiVersion = "v1",
                Metadata = new V1ObjectMeta
                {
                    Name = "orders-api-def456",
                    NamespaceProperty = "orders-prod"
                },
                Status = new V1PodStatus
                {
                    Phase = "Pending",
                    PodIP = "10.244.0.16"
                }
            }
        ];

        var preview = KubeActionPreviewFactory.CreateScaleDeploymentPreview(
            "kind-kuberkynesis-lab",
            deployment,
            pods,
            targetReplicas: 1);

        Assert.Equal(KubeActionKind.ScaleDeployment, preview.Action);
        Assert.Equal("orders-api", preview.Resource.Name);
        Assert.Contains("3 -> 1 replicas", preview.Summary, StringComparison.Ordinal);
        Assert.Equal(KubeActionPreviewConfidence.Low, preview.Confidence);
        Assert.Equal(KubeActionRiskLevel.High, preview.Guardrails.RiskLevel);
        Assert.Equal(KubeActionConfirmationLevel.TypedConfirmationWithScope, preview.Guardrails.ConfirmationLevel);
        Assert.False(preview.Guardrails.IsExecutionBlocked);
        Assert.Contains("limited", preview.CoverageSummary, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(preview.CoverageLimits);
        Assert.Contains(preview.CoverageLimits, limit => limit.Contains("schedulers, quotas, or node capacity", StringComparison.Ordinal));
        Assert.Contains(preview.CoverageLimits, limit => limit.Contains("Autoscalers, disruption budgets", StringComparison.Ordinal));
        Assert.Contains(preview.Facts, fact => fact.Label == "Current replicas" && fact.Value == "3");
        Assert.Contains(preview.Facts, fact => fact.Label == "Target replicas" && fact.Value == "1");
        Assert.Contains(preview.Facts, fact => fact.Label == "Selector" && fact.Value == "app=orders-api");
        Assert.Contains(preview.Warnings, warning => warning.Contains("Scaling down by 2", StringComparison.Ordinal));
        Assert.Contains(preview.Warnings, warning => warning.Contains("Only 2/3 replicas are ready", StringComparison.Ordinal));
        Assert.Contains(preview.Warnings, warning => warning.Contains("paused", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(preview.SaferAlternatives, alternative => alternative.Label == "Scale to 2");
        Assert.Contains(preview.SaferAlternatives, alternative => alternative.Label == "Inspect rollout health first");
        Assert.Equal(2, preview.AffectedResources.Count);
        Assert.Equal("Current pod", preview.AffectedResources[0].Relationship);
        Assert.Equal(
            "kubectl --context kind-kuberkynesis-lab -n orders-prod scale deployment/orders-api --replicas=1",
            Assert.Single(preview.TransparencyCommands).Command);
    }

    [Fact]
    public void CreateRestartDeploymentPreview_BuildsRestartFactsAndCommand()
    {
        var deployment = new V1Deployment
        {
            ApiVersion = "apps/v1",
            Metadata = new V1ObjectMeta
            {
                Name = "orders-api",
                NamespaceProperty = "orders-prod"
            },
            Spec = new V1DeploymentSpec
            {
                Replicas = 3,
                Selector = new V1LabelSelector
                {
                    MatchLabels = new Dictionary<string, string>
                    {
                        ["app"] = "orders-api"
                    }
                }
            },
            Status = new V1DeploymentStatus
            {
                ReadyReplicas = 3,
                AvailableReplicas = 3,
                UpdatedReplicas = 3
            }
        };

        V1Pod[] pods =
        [
            new V1Pod
            {
                ApiVersion = "v1",
                Metadata = new V1ObjectMeta
                {
                    Name = "orders-api-abc123",
                    NamespaceProperty = "orders-prod"
                },
                Status = new V1PodStatus
                {
                    Phase = "Running"
                }
            }
        ];

        var preview = KubeActionPreviewFactory.CreateRestartDeploymentPreview(
            "kind-kuberkynesis-lab",
            deployment,
            pods,
            new KubePodDisruptionBudgetImpact([], 0, 0, 0));

        Assert.Equal(KubeActionKind.RestartDeploymentRollout, preview.Action);
        Assert.Equal(KubeActionPreviewConfidence.Medium, preview.Confidence);
        Assert.Equal(KubeActionRiskLevel.Medium, preview.Guardrails.RiskLevel);
        Assert.Equal(KubeActionConfirmationLevel.TypedConfirmation, preview.Guardrails.ConfirmationLevel);
        Assert.False(preview.Guardrails.IsExecutionBlocked);
        Assert.NotEmpty(preview.CoverageLimits);
        Assert.Contains(preview.CoverageLimits, limit => limit.Contains("replacement order", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("rollout restart", preview.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(preview.Facts, fact => fact.Label == "Strategy" &&
                                               fact.Value.Contains("RollingUpdate", StringComparison.Ordinal));
        Assert.Contains(preview.Facts, fact => fact.Label == "Desired replicas" && fact.Value == "3");
        Assert.Contains(preview.Facts, fact => fact.Label == "Cluster context" && fact.Value == "kind-kuberkynesis-lab");
        Assert.Contains(preview.Facts, fact => fact.Label == "Scope boundary" && fact.Value == "Namespace orders-prod");
        Assert.Contains(preview.Facts, fact => fact.Label == "Affected namespaces" &&
                                               fact.Value.Contains("orders-prod", StringComparison.Ordinal));
        Assert.Contains(preview.SaferAlternatives, alternative => alternative.Label == "Inspect current rollout health first");
        Assert.Equal(
            "kubectl --context kind-kuberkynesis-lab -n orders-prod rollout restart deployment/orders-api",
            Assert.Single(preview.TransparencyCommands).Command);
    }

    [Fact]
    public void CreateRestartDeploymentPreview_WithRecreateStrategy_AddsStrategyWarning()
    {
        var deployment = new V1Deployment
        {
            ApiVersion = "apps/v1",
            Metadata = new V1ObjectMeta
            {
                Name = "orders-api",
                NamespaceProperty = "orders-prod"
            },
            Spec = new V1DeploymentSpec
            {
                Replicas = 2,
                Selector = new V1LabelSelector
                {
                    MatchLabels = new Dictionary<string, string>
                    {
                        ["app.kubernetes.io/name"] = "orders-api"
                    }
                },
                Strategy = new V1DeploymentStrategy
                {
                    Type = "Recreate"
                }
            },
            Status = new V1DeploymentStatus
            {
                ReadyReplicas = 2,
                AvailableReplicas = 2,
                UpdatedReplicas = 2
            }
        };

        var preview = KubeActionPreviewFactory.CreateRestartDeploymentPreview(
            "kind-kuberkynesis-lab",
            deployment,
            [],
            new KubePodDisruptionBudgetImpact([], 0, 0, 0));

        Assert.Contains(preview.Facts, fact => fact.Label == "Strategy" && fact.Value == "Recreate");
        Assert.Contains(preview.Warnings, warning => warning.Contains("Recreate strategy", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CreateRestartDeploymentPreview_ForBroadReplicaScope_RaisesDangerousBulkRisk()
    {
        var deployment = new V1Deployment
        {
            ApiVersion = "apps/v1",
            Metadata = new V1ObjectMeta
            {
                Name = "orders-api",
                NamespaceProperty = "orders-dev"
            },
            Spec = new V1DeploymentSpec
            {
                Replicas = 12,
                Selector = new V1LabelSelector
                {
                    MatchLabels = new Dictionary<string, string>
                    {
                        ["app.kubernetes.io/name"] = "orders-api"
                    }
                }
            },
            Status = new V1DeploymentStatus
            {
                ReadyReplicas = 12,
                AvailableReplicas = 12,
                UpdatedReplicas = 12
            }
        };

        var preview = KubeActionPreviewFactory.CreateRestartDeploymentPreview(
            "kind-kuberkynesis-lab",
            deployment,
            [],
            new KubePodDisruptionBudgetImpact([], 0, 0, 0));

        Assert.Equal(KubeActionRiskLevel.High, preview.Guardrails.RiskLevel);
        Assert.Contains(preview.Guardrails.Reasons, reason => reason.Contains("12 targets", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(preview.Guardrails.Reasons, reason => reason.Contains("dangerous bulk scope", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CreateRollbackDeploymentPreview_BuildsRetainedRevisionFactsAndCommand()
    {
        var deployment = new V1Deployment
        {
            ApiVersion = "apps/v1",
            Metadata = new V1ObjectMeta
            {
                Name = "orders-api",
                NamespaceProperty = "orders-prod",
                Annotations = new Dictionary<string, string>
                {
                    ["deployment.kubernetes.io/revision"] = "9"
                }
            },
            Spec = new V1DeploymentSpec
            {
                Replicas = 3,
                Selector = new V1LabelSelector
                {
                    MatchLabels = new Dictionary<string, string>
                    {
                        ["app"] = "orders-api"
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
                                Image = "orders-api:v3"
                            }
                        ]
                    }
                }
            },
            Status = new V1DeploymentStatus
            {
                ReadyReplicas = 3,
                AvailableReplicas = 3,
                UpdatedReplicas = 3
            }
        };

        V1Pod[] pods =
        [
            new V1Pod
            {
                ApiVersion = "v1",
                Metadata = new V1ObjectMeta
                {
                    Name = "orders-api-abc123",
                    NamespaceProperty = "orders-prod"
                },
                Status = new V1PodStatus
                {
                    Phase = "Running"
                }
            }
        ];

        var rollbackResolution = new KubeDeploymentRollbackResolution(
            CurrentReplicaSet: new V1ReplicaSet
            {
                Metadata = new V1ObjectMeta
                {
                    Name = "orders-api-6f4d9b4c8d",
                    NamespaceProperty = "orders-prod"
                }
            },
            PreviousReplicaSet: new V1ReplicaSet
            {
                ApiVersion = "apps/v1",
                Metadata = new V1ObjectMeta
                {
                    Name = "orders-api-74cc9f49f4",
                    NamespaceProperty = "orders-prod"
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
                                    Image = "orders-api:v2"
                                }
                            ]
                        }
                    }
                }
            },
            CurrentRevision: 9,
            PreviousRevision: 8,
            RetainedRevisionCount: 3,
            UsedReplicaSetRevisionFallback: false,
            PreviousChangeCause: "deploy v2");

        var preview = KubeActionPreviewFactory.CreateRollbackDeploymentPreview(
            "kind-kuberkynesis-lab",
            deployment,
            pods,
            rollbackResolution,
            new KubePodDisruptionBudgetImpact([], 0, 0, 0),
            rollbackHistoryCoverageRestricted: false);

        Assert.Equal(KubeActionKind.RollbackDeploymentRollout, preview.Action);
        Assert.Equal(KubeActionPreviewConfidence.Medium, preview.Confidence);
        Assert.Equal(KubeActionRiskLevel.Medium, preview.Guardrails.RiskLevel);
        Assert.Equal(KubeActionConfirmationLevel.TypedConfirmation, preview.Guardrails.ConfirmationLevel);
        Assert.False(preview.Guardrails.IsExecutionBlocked);
        Assert.Contains("retained revision 8", preview.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(preview.Facts, fact => fact.Label == "Current revision" && fact.Value == "9");
        Assert.Contains(preview.Facts, fact => fact.Label == "Rollback target revision" && fact.Value == "8");
        Assert.Contains(preview.Facts, fact => fact.Label == "Rollback target" && fact.Value == "ReplicaSet/orders-api-74cc9f49f4");
        Assert.Contains(preview.Facts, fact => fact.Label == "Rollback target images" && fact.Value.Contains("orders-api:v2", StringComparison.Ordinal));
        Assert.Contains(preview.Facts, fact => fact.Label == "Target change cause" && fact.Value == "deploy v2");
        Assert.Contains(preview.SaferAlternatives, alternative => alternative.Label == "Preview rollout restart instead");
        Assert.Equal(
            "kubectl --context kind-kuberkynesis-lab -n orders-prod rollout undo deployment/orders-api",
            Assert.Single(preview.TransparencyCommands).Command);
    }

    [Fact]
    public void CreateRollbackDeploymentPreview_WithoutRetainedRevision_BlocksExecution()
    {
        var deployment = new V1Deployment
        {
            Metadata = new V1ObjectMeta
            {
                Name = "orders-api",
                NamespaceProperty = "orders-prod",
                Annotations = new Dictionary<string, string>
                {
                    ["deployment.kubernetes.io/revision"] = "4"
                }
            },
            Spec = new V1DeploymentSpec
            {
                Replicas = 2
            },
            Status = new V1DeploymentStatus
            {
                ReadyReplicas = 2,
                AvailableReplicas = 2,
                UpdatedReplicas = 2
            }
        };

        var preview = KubeActionPreviewFactory.CreateRollbackDeploymentPreview(
            "kind-kuberkynesis-lab",
            deployment,
            [],
            new KubeDeploymentRollbackResolution(
                CurrentReplicaSet: null,
                PreviousReplicaSet: null,
                CurrentRevision: 4,
                PreviousRevision: null,
                RetainedRevisionCount: 1,
                UsedReplicaSetRevisionFallback: false,
                PreviousChangeCause: null),
            new KubePodDisruptionBudgetImpact([], 0, 0, 0),
            rollbackHistoryCoverageRestricted: false);

        Assert.True(preview.Guardrails.IsExecutionBlocked);
        Assert.Equal(KubeActionRiskLevel.Informational, preview.Guardrails.RiskLevel);
        Assert.Contains("no retained prior revision", preview.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(preview.Warnings, warning => warning.Contains("No retained prior revision", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(preview.CoverageLimits, limit => limit.Contains("No retained prior ReplicaSet template", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CreateDeletePodPreview_BuildsControllerAwareFactsAndCommand()
    {
        var pod = new V1Pod
        {
            ApiVersion = "v1",
            Metadata = new V1ObjectMeta
            {
                Name = "orders-api-abc123",
                NamespaceProperty = "orders-prod",
                Labels = new Dictionary<string, string>
                {
                    ["env"] = "prod"
                }
            },
            Spec = new V1PodSpec
            {
                NodeName = "kuberkynesis-lab-worker"
            },
            Status = new V1PodStatus
            {
                Phase = "Running",
                PodIP = "10.244.0.15",
                ContainerStatuses =
                [
                    new V1ContainerStatus
                    {
                        Name = "app",
                        RestartCount = 2
                    }
                ]
            }
        };

        var immediateOwner = new KubeRelatedResource(
            Relationship: "Immediate owner",
            Kind: KubeResourceKind.ReplicaSet,
            ApiVersion: "apps/v1",
            Name: "orders-api-5d4566bdf6",
            Namespace: "orders-prod",
            Status: null,
            Summary: null);

        var rolloutOwner = new KubeRelatedResource(
            Relationship: "Rollout owner",
            Kind: KubeResourceKind.Deployment,
            ApiVersion: "apps/v1",
            Name: "orders-api",
            Namespace: "orders-prod",
            Status: null,
            Summary: null);

        var preview = KubeActionPreviewFactory.CreateDeletePodPreview(
            "kind-kuberkynesis-lab",
            pod,
            immediateOwner,
            rolloutOwner,
            replacementLikely: true,
            new KubePodDisruptionBudgetImpact([], 0, 0, 0));

        Assert.Equal(KubeActionKind.DeletePod, preview.Action);
        Assert.Equal(KubeActionPreviewConfidence.High, preview.Confidence);
        Assert.Equal(KubeActionRiskLevel.Medium, preview.Guardrails.RiskLevel);
        Assert.Equal(KubeActionConfirmationLevel.TypedConfirmation, preview.Guardrails.ConfirmationLevel);
        Assert.False(preview.Guardrails.IsExecutionBlocked);
        Assert.Equal("Current-state coverage is strong for the current target and controller evidence.", preview.CoverageSummary);
        Assert.DoesNotContain("scale comparison", preview.CoverageSummary, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(preview.CoverageLimits);
        Assert.Contains(preview.CoverageLimits, limit => limit.Contains("connection drains", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(preview.Facts, fact => fact.Label == "Controlled by" && fact.Value == "Deployment/orders-api");
        Assert.Contains(preview.SaferAlternatives, alternative => alternative.Label == "Preview rollout restart for orders-api");
        Assert.Equal(2, preview.AffectedResources.Count);
        Assert.Equal(
            "kubectl --context kind-kuberkynesis-lab -n orders-prod delete pod/orders-api-abc123",
            Assert.Single(preview.TransparencyCommands).Command);
    }

    [Fact]
    public void CreateScaleDeploymentPreview_ForNonProductionScaleUp_KeepsGuardrailsInline()
    {
        var deployment = new V1Deployment
        {
            ApiVersion = "apps/v1",
            Metadata = new V1ObjectMeta
            {
                Name = "orders-api",
                NamespaceProperty = "orders-dev"
            },
            Spec = new V1DeploymentSpec
            {
                Replicas = 2,
                Selector = new V1LabelSelector
                {
                    MatchLabels = new Dictionary<string, string>
                    {
                        ["app"] = "orders-api"
                    }
                }
            },
            Status = new V1DeploymentStatus
            {
                ReadyReplicas = 2,
                AvailableReplicas = 2,
                UpdatedReplicas = 2
            }
        };

        V1Pod[] pods =
        [
            new V1Pod
            {
                ApiVersion = "v1",
                Metadata = new V1ObjectMeta
                {
                    Name = "orders-api-abc123",
                    NamespaceProperty = "orders-dev"
                },
                Status = new V1PodStatus
                {
                    Phase = "Running"
                }
            },
            new V1Pod
            {
                ApiVersion = "v1",
                Metadata = new V1ObjectMeta
                {
                    Name = "orders-api-def456",
                    NamespaceProperty = "orders-dev"
                },
                Status = new V1PodStatus
                {
                    Phase = "Running"
                }
            }
        ];

        var preview = KubeActionPreviewFactory.CreateScaleDeploymentPreview(
            "kind-kuberkynesis-lab",
            deployment,
            pods,
            targetReplicas: 3);

        Assert.Equal(KubeActionPreviewConfidence.Medium, preview.Confidence);
        Assert.Equal(KubeActionRiskLevel.Low, preview.Guardrails.RiskLevel);
        Assert.Equal(KubeActionConfirmationLevel.InlineSummary, preview.Guardrails.ConfirmationLevel);
        Assert.False(preview.Guardrails.IsExecutionBlocked);
        Assert.Null(preview.Guardrails.AcknowledgementHint);
        Assert.DoesNotContain(preview.Guardrails.Reasons, reason => reason.Contains("production-like", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("standard confirm path", preview.Guardrails.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            "kubectl --context kind-kuberkynesis-lab -n orders-dev scale deployment/orders-api --replicas=3",
            Assert.Single(preview.TransparencyCommands).Command);
    }

    [Fact]
    public void CreateScaleDeploymentPreview_WhenTargetMatchesCurrent_BlocksExecutionAsNoOp()
    {
        var deployment = new V1Deployment
        {
            ApiVersion = "apps/v1",
            Metadata = new V1ObjectMeta
            {
                Name = "orders-api",
                NamespaceProperty = "orders-prod"
            },
            Spec = new V1DeploymentSpec
            {
                Replicas = 7,
                Selector = new V1LabelSelector
                {
                    MatchLabels = new Dictionary<string, string>
                    {
                        ["app"] = "orders-api"
                    }
                }
            },
            Status = new V1DeploymentStatus
            {
                ReadyReplicas = 7,
                AvailableReplicas = 7,
                UpdatedReplicas = 7
            }
        };

        V1Pod[] pods = [];

        var preview = KubeActionPreviewFactory.CreateScaleDeploymentPreview(
            "kind-kuberkynesis-lab",
            deployment,
            pods,
            targetReplicas: 7);

        Assert.Equal(KubeActionKind.ScaleDeployment, preview.Action);
        Assert.True(preview.Guardrails.IsExecutionBlocked);
        Assert.Equal(KubeActionRiskLevel.Informational, preview.Guardrails.RiskLevel);
        Assert.Equal(KubeActionConfirmationLevel.InlineSummary, preview.Guardrails.ConfirmationLevel);
        Assert.Null(preview.Guardrails.AcknowledgementHint);
        Assert.Contains("already set to 7 replicas", preview.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("would not change desired count", preview.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("already matches the live deployment", preview.Guardrails.Reasons[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateScaleStatefulSetPreview_UsesTypedConfirmationForProductionScaleChanges()
    {
        var statefulSet = new V1StatefulSet
        {
            ApiVersion = "apps/v1",
            Metadata = new V1ObjectMeta
            {
                Name = "orders-db",
                NamespaceProperty = "orders-prod"
            },
            Spec = new V1StatefulSetSpec
            {
                Replicas = 3,
                Selector = new V1LabelSelector
                {
                    MatchLabels = new Dictionary<string, string>
                    {
                        ["app"] = "orders-db"
                    }
                }
            },
            Status = new V1StatefulSetStatus
            {
                ReadyReplicas = 3,
                AvailableReplicas = 3,
                UpdatedReplicas = 3
            }
        };

        V1Pod[] pods =
        [
            new V1Pod
            {
                ApiVersion = "v1",
                Metadata = new V1ObjectMeta
                {
                    Name = "orders-db-0",
                    NamespaceProperty = "orders-prod"
                },
                Status = new V1PodStatus
                {
                    Phase = "Running"
                }
            }
        ];

        var preview = KubeActionPreviewFactory.CreateScaleStatefulSetPreview(
            "kind-kuberkynesis-lab",
            statefulSet,
            pods,
            targetReplicas: 2);

        Assert.Equal(KubeActionKind.ScaleStatefulSet, preview.Action);
        Assert.Equal(KubeActionAvailability.PreviewAndExecute, preview.Availability);
        Assert.Equal(KubeActionEnvironmentKind.Production, preview.Environment);
        Assert.Equal(KubeActionRiskLevel.Medium, preview.Guardrails.RiskLevel);
        Assert.Equal(KubeActionConfirmationLevel.TypedConfirmation, preview.Guardrails.ConfirmationLevel);
        Assert.Contains(preview.SaferAlternatives, alternative => alternative.Label == "Keep the current replica count" &&
                                                                   alternative.Reason.Contains("live workload view", StringComparison.Ordinal));
        Assert.DoesNotContain(preview.SaferAlternatives, alternative => alternative.Reason.Contains("live deployment view", StringComparison.Ordinal));
        Assert.Equal(
            "kubectl --context kind-kuberkynesis-lab -n orders-prod scale statefulset/orders-db --replicas=2",
            Assert.Single(preview.TransparencyCommands).Command);
    }

    [Fact]
    public void CreateScaleStatefulSetPreview_WhenTargetMatchesCurrent_BlocksExecutionAsNoOp()
    {
        var statefulSet = new V1StatefulSet
        {
            ApiVersion = "apps/v1",
            Metadata = new V1ObjectMeta
            {
                Name = "orders-db",
                NamespaceProperty = "orders-prod"
            },
            Spec = new V1StatefulSetSpec
            {
                Replicas = 3,
                Selector = new V1LabelSelector
                {
                    MatchLabels = new Dictionary<string, string>
                    {
                        ["app"] = "orders-db"
                    }
                }
            },
            Status = new V1StatefulSetStatus
            {
                ReadyReplicas = 3,
                AvailableReplicas = 3,
                UpdatedReplicas = 3
            }
        };

        V1Pod[] pods = [];

        var preview = KubeActionPreviewFactory.CreateScaleStatefulSetPreview(
            "kind-kuberkynesis-lab",
            statefulSet,
            pods,
            targetReplicas: 3);

        Assert.Equal(KubeActionKind.ScaleStatefulSet, preview.Action);
        Assert.True(preview.Guardrails.IsExecutionBlocked);
        Assert.Equal(KubeActionRiskLevel.Informational, preview.Guardrails.RiskLevel);
        Assert.Equal(KubeActionConfirmationLevel.InlineSummary, preview.Guardrails.ConfirmationLevel);
        Assert.Null(preview.Guardrails.AcknowledgementHint);
        Assert.Contains("already set to 3 replicas", preview.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("would not change desired count", preview.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("already matches the live StatefulSet", preview.Guardrails.Reasons[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateDeleteJobPreview_ForCompletedJob_UsesCompletedRecordGuidance()
    {
        var job = new V1Job
        {
            ApiVersion = "batch/v1",
            Metadata = new V1ObjectMeta
            {
                Name = "orders-reconciliation-29582040",
                NamespaceProperty = "orders-prod"
            },
            Spec = new V1JobSpec
            {
                Completions = 1
            },
            Status = new V1JobStatus
            {
                Active = 0,
                Succeeded = 1,
                Failed = 0
            }
        };

        var preview = KubeActionPreviewFactory.CreateDeleteJobPreview(
            "kind-kuberkynesis-lab",
            job);

        Assert.Equal(KubeActionKind.DeleteJob, preview.Action);
        Assert.Equal(KubeActionPreviewConfidence.High, preview.Confidence);
        Assert.Equal(KubeActionRiskLevel.Medium, preview.Guardrails.RiskLevel);
        Assert.Contains(preview.Notes, note => note.Contains("completion history", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(preview.SaferAlternatives, alternative => alternative.Label == "Keep the completed job record" &&
                                                                   alternative.Reason.Contains("completion evidence", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(preview.SaferAlternatives, alternative => alternative.Reason.Contains("remaining job attempt complete", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CreateCronJobSuspendPreview_WithoutActiveJobs_UsesRecentHistoryGuidance()
    {
        var cronJob = new V1CronJob
        {
            ApiVersion = "batch/v1",
            Metadata = new V1ObjectMeta
            {
                Name = "orders-reconciliation",
                NamespaceProperty = "orders-prod"
            },
            Spec = new V1CronJobSpec
            {
                Schedule = "*/15 * * * *",
                Suspend = false
            },
            Status = new V1CronJobStatus
            {
                Active = []
            }
        };

        var preview = KubeActionPreviewFactory.CreateCronJobSuspendPreview(
            "kind-kuberkynesis-lab",
            cronJob,
            suspend: true);

        Assert.Equal(KubeActionKind.SuspendCronJob, preview.Action);
        Assert.Equal(KubeActionPreviewConfidence.High, preview.Confidence);
        Assert.Contains(preview.SaferAlternatives, alternative => alternative.Label == "Inspect recent job history first" &&
                                                                   alternative.Reason.Contains("recent runs", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(preview.SaferAlternatives, alternative => alternative.Label == "Inspect active jobs first");
    }

    [Fact]
    public void CreateCronJobResumePreview_WhenAlreadyActive_BlocksExecutionAsNoOp()
    {
        var cronJob = new V1CronJob
        {
            ApiVersion = "batch/v1",
            Metadata = new V1ObjectMeta
            {
                Name = "orders-reconciliation",
                NamespaceProperty = "orders-prod"
            },
            Spec = new V1CronJobSpec
            {
                Schedule = "*/15 * * * *",
                Suspend = false
            },
            Status = new V1CronJobStatus
            {
                Active = []
            }
        };

        var preview = KubeActionPreviewFactory.CreateCronJobSuspendPreview(
            "kind-kuberkynesis-lab",
            cronJob,
            suspend: false);

        Assert.Equal(KubeActionKind.ResumeCronJob, preview.Action);
        Assert.True(preview.Guardrails.IsExecutionBlocked);
        Assert.Equal(KubeActionRiskLevel.Informational, preview.Guardrails.RiskLevel);
        Assert.Equal(KubeActionConfirmationLevel.InlineSummary, preview.Guardrails.ConfirmationLevel);
        Assert.Null(preview.Guardrails.AcknowledgementHint);
        Assert.Contains("would not change scheduling", preview.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("already active", preview.Guardrails.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(preview.Guardrails.Reasons, reason => reason.Contains("already matches", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildImpact_MatchesPodDisruptionBudgetsFromLabelsAndExpressions()
    {
        V1Pod[] pods =
        [
            new V1Pod
            {
                Metadata = new V1ObjectMeta
                {
                    Name = "orders-api-0",
                    NamespaceProperty = "orders-prod",
                    Labels = new Dictionary<string, string>
                    {
                        ["app.kubernetes.io/name"] = "orders-api",
                        ["version"] = "3"
                    }
                }
            }
        ];

        V1PodDisruptionBudget[] budgets =
        [
            new()
            {
                ApiVersion = "policy/v1",
                Metadata = new V1ObjectMeta
                {
                    Name = "orders-api-pdb",
                    NamespaceProperty = "orders-prod"
                },
                Spec = new V1PodDisruptionBudgetSpec
                {
                    Selector = new V1LabelSelector
                    {
                        MatchLabels = new Dictionary<string, string>
                        {
                            ["app.kubernetes.io/name"] = "orders-api"
                        }
                    }
                },
                Status = new V1PodDisruptionBudgetStatus
                {
                    DisruptionsAllowed = 0,
                    CurrentHealthy = 2,
                    DesiredHealthy = 2
                }
            },
            new()
            {
                ApiVersion = "policy/v1",
                Metadata = new V1ObjectMeta
                {
                    Name = "version-pdb",
                    NamespaceProperty = "orders-prod"
                },
                Spec = new V1PodDisruptionBudgetSpec
                {
                    Selector = new V1LabelSelector
                    {
                        MatchExpressions =
                        [
                            new V1LabelSelectorRequirement
                            {
                                Key = "version",
                                OperatorProperty = "In",
                                Values = ["3"]
                            }
                        ]
                    }
                },
                Status = new V1PodDisruptionBudgetStatus
                {
                    DisruptionsAllowed = 1,
                    CurrentHealthy = 2,
                    DesiredHealthy = 1
                }
            }
        ];

        var impact = KubePodDisruptionBudgetMatcher.BuildImpact(budgets, pods);

        Assert.True(impact.HasMatchedBudgets);
        Assert.Equal(2, impact.MatchedBudgetCount);
        Assert.Equal(1, impact.ZeroDisruptionsAllowedCount);
        Assert.Equal(0, impact.UnknownAllowanceCount);
        Assert.Contains(impact.RelatedResources, resource => resource.Name == "orders-api-pdb" &&
                                                             resource.Relationship == "Matched PDB" &&
                                                             resource.ApiVersion == "PodDisruptionBudget");
        Assert.Contains(impact.RelatedResources, resource => resource.Name == "version-pdb");
    }

    [Fact]
    public void CreateRestartDeploymentPreview_WithMatchedDisruptionBudget_AddsBudgetContext()
    {
        var deployment = new V1Deployment
        {
            ApiVersion = "apps/v1",
            Metadata = new V1ObjectMeta
            {
                Name = "orders-api",
                NamespaceProperty = "orders-prod"
            },
            Spec = new V1DeploymentSpec
            {
                Replicas = 2,
                Selector = new V1LabelSelector
                {
                    MatchLabels = new Dictionary<string, string>
                    {
                        ["app.kubernetes.io/name"] = "orders-api"
                    }
                }
            },
            Status = new V1DeploymentStatus
            {
                ReadyReplicas = 2,
                AvailableReplicas = 2,
                UpdatedReplicas = 2
            }
        };

        V1Pod[] pods =
        [
            new V1Pod
            {
                Metadata = new V1ObjectMeta
                {
                    Name = "orders-api-0",
                    NamespaceProperty = "orders-prod",
                    Labels = new Dictionary<string, string>
                    {
                        ["app.kubernetes.io/name"] = "orders-api"
                    }
                },
                Status = new V1PodStatus
                {
                    Phase = "Running"
                }
            }
        ];

        var disruptionBudgetImpact = KubePodDisruptionBudgetMatcher.BuildImpact(
            [
                new V1PodDisruptionBudget
                {
                    ApiVersion = "policy/v1",
                    Metadata = new V1ObjectMeta
                    {
                        Name = "orders-api-pdb",
                        NamespaceProperty = "orders-prod"
                    },
                    Spec = new V1PodDisruptionBudgetSpec
                    {
                        Selector = new V1LabelSelector
                        {
                            MatchLabels = new Dictionary<string, string>
                            {
                                ["app.kubernetes.io/name"] = "orders-api"
                            }
                        }
                    },
                    Status = new V1PodDisruptionBudgetStatus
                    {
                        DisruptionsAllowed = 0,
                        CurrentHealthy = 2,
                        DesiredHealthy = 2
                    }
                }
            ],
            pods);

        var preview = KubeActionPreviewFactory.CreateRestartDeploymentPreview(
            "kind-kuberkynesis-lab",
            deployment,
            pods,
            disruptionBudgetImpact);

        Assert.Contains(preview.Warnings, warning => warning.Contains("PodDisruptionBudget", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(preview.AffectedResources, resource => resource.Relationship == "Matched PDB" && resource.Name == "orders-api-pdb");
        Assert.Contains(preview.Guardrails.Reasons, reason => reason.Contains("matched PodDisruptionBudget", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(preview.CoverageLimits, limit => limit.Contains("Matched PodDisruptionBudget status", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CreateRestartDaemonSetPreview_WithOnDeleteStrategy_AddsStrategyWarning()
    {
        var daemonSet = new V1DaemonSet
        {
            ApiVersion = "apps/v1",
            Metadata = new V1ObjectMeta
            {
                Name = "kube-proxy",
                NamespaceProperty = "kube-system"
            },
            Spec = new V1DaemonSetSpec
            {
                UpdateStrategy = new V1DaemonSetUpdateStrategy
                {
                    Type = "OnDelete"
                }
            },
            Status = new V1DaemonSetStatus
            {
                DesiredNumberScheduled = 2,
                NumberReady = 2,
                NumberAvailable = 2,
                NumberUnavailable = 0
            }
        };

        V1Pod[] pods =
        [
            new V1Pod
            {
                Metadata = new V1ObjectMeta
                {
                    Name = "kube-proxy-a",
                    NamespaceProperty = "kube-system"
                },
                Status = new V1PodStatus
                {
                    Phase = "Running"
                }
            }
        ];

        var preview = KubeActionPreviewFactory.CreateRestartDaemonSetPreview(
            "kind-kuberkynesis-lab",
            daemonSet,
            pods,
            new KubePodDisruptionBudgetImpact([], 0, 0, 0));

        Assert.Contains(preview.Facts, fact => fact.Label == "Update strategy" && fact.Value == "OnDelete");
        Assert.Contains(preview.Warnings, warning => warning.Contains("manual deletion", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CreateDeletePodPreview_WithMatchedDisruptionBudget_AddsDirectDeleteWarning()
    {
        var pod = new V1Pod
        {
            ApiVersion = "v1",
            Metadata = new V1ObjectMeta
            {
                Name = "orders-api-abc123",
                NamespaceProperty = "orders-prod",
                Labels = new Dictionary<string, string>
                {
                    ["app.kubernetes.io/name"] = "orders-api"
                }
            },
            Spec = new V1PodSpec
            {
                NodeName = "kuberkynesis-lab-worker"
            },
            Status = new V1PodStatus
            {
                Phase = "Running"
            }
        };

        var rolloutOwner = new KubeRelatedResource(
            Relationship: "Rollout owner",
            Kind: KubeResourceKind.Deployment,
            ApiVersion: "apps/v1",
            Name: "orders-api",
            Namespace: "orders-prod",
            Status: null,
            Summary: null);

        var disruptionBudgetImpact = KubePodDisruptionBudgetMatcher.BuildImpact(
            [
                new V1PodDisruptionBudget
                {
                    ApiVersion = "policy/v1",
                    Metadata = new V1ObjectMeta
                    {
                        Name = "orders-api-pdb",
                        NamespaceProperty = "orders-prod"
                    },
                    Spec = new V1PodDisruptionBudgetSpec
                    {
                        Selector = new V1LabelSelector
                        {
                            MatchLabels = new Dictionary<string, string>
                            {
                                ["app.kubernetes.io/name"] = "orders-api"
                            }
                        }
                    },
                    Status = new V1PodDisruptionBudgetStatus
                    {
                        DisruptionsAllowed = 0,
                        CurrentHealthy = 2,
                        DesiredHealthy = 2
                    }
                }
            ],
            [pod]);

        var preview = KubeActionPreviewFactory.CreateDeletePodPreview(
            "kind-kuberkynesis-lab",
            pod,
            immediateOwner: null,
            rolloutOwner,
            replacementLikely: true,
            disruptionBudgetImpact);

        Assert.Contains(preview.Warnings, warning => warning.Contains("direct pod delete", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(preview.Notes, note => note.Contains("bypass eviction-style budget enforcement", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(preview.CoverageLimits, limit => limit.Contains("bypass eviction-style disruption gating", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(preview.AffectedResources, resource => resource.Relationship == "Matched PDB" && resource.Name == "orders-api-pdb");
    }

    [Fact]
    public void CreateNodeUncordonPreview_WhenAlreadySchedulable_BlocksExecutionAsNoOp()
    {
        var node = new V1Node
        {
            Metadata = new V1ObjectMeta
            {
                Name = "kuberkynesis-lab-control-plane"
            },
            Spec = new V1NodeSpec
            {
                Unschedulable = false
            }
        };

        V1Pod[] pods =
        [
            new V1Pod
            {
                Metadata = new V1ObjectMeta
                {
                    Name = "kube-proxy-2djml",
                    NamespaceProperty = "kube-system"
                },
                Status = new V1PodStatus
                {
                    Phase = "Running"
                }
            },
            new V1Pod
            {
                Metadata = new V1ObjectMeta
                {
                    Name = "kube-proxy-worker",
                    NamespaceProperty = "kube-system"
                },
                Status = new V1PodStatus
                {
                    Phase = "Running"
                }
            }
        ];

        var preview = KubeActionPreviewFactory.CreateNodeSchedulingPreview(
            "kind-kuberkynesis-lab",
            node,
            pods,
            cordon: false);

        Assert.Equal(KubeActionKind.UncordonNode, preview.Action);
        Assert.True(preview.Guardrails.IsExecutionBlocked);
        Assert.Equal(KubeActionRiskLevel.Informational, preview.Guardrails.RiskLevel);
        Assert.Equal(KubeActionConfirmationLevel.InlineSummary, preview.Guardrails.ConfirmationLevel);
        Assert.Null(preview.Guardrails.AcknowledgementHint);
        Assert.Contains("already schedulable", preview.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("would not change schedulability", preview.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("already schedulable", preview.Guardrails.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(preview.Notes, note => note.Contains("future placements", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(preview.Notes, note => note.StartsWith("Cordon changes", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CreateDrainNodePreview_IsPreviewOnlyAndBlocked()
    {
        var node = new V1Node
        {
            Metadata = new V1ObjectMeta
            {
                Name = "kuberkynesis-lab-worker"
            }
        };

        V1Pod[] pods =
        [
            new V1Pod
            {
                Metadata = new V1ObjectMeta
                {
                    Name = "orders-api-0",
                    NamespaceProperty = "orders-prod"
                },
                Status = new V1PodStatus
                {
                    Phase = "Running"
                }
            }
        ];

        var preview = KubeActionPreviewFactory.CreateDrainNodePreview(
            "kind-kuberkynesis-lab",
            node,
            pods,
            new KubePodDisruptionBudgetImpact([], 0, 0, 0));

        Assert.Equal(KubeActionKind.DrainNode, preview.Action);
        Assert.Equal(KubeActionAvailability.PreviewOnly, preview.Availability);
        Assert.True(preview.Guardrails.IsExecutionBlocked);
        Assert.Equal(KubeActionRiskLevel.Critical, preview.Guardrails.RiskLevel);
        Assert.Equal(KubeActionConfirmationLevel.ExplicitReview, preview.Guardrails.ConfirmationLevel);
        Assert.Null(preview.Guardrails.AcknowledgementHint);
        Assert.Contains("stops at preview", preview.Guardrails.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            "kubectl --context kind-kuberkynesis-lab drain kuberkynesis-lab-worker --ignore-daemonsets --delete-emptydir-data",
            Assert.Single(preview.TransparencyCommands).Command);
    }

    [Fact]
    public void CreateNodeSchedulingPreview_ForBroadClusterScope_AddsDangerousBulkReasons()
    {
        var node = new V1Node
        {
            Metadata = new V1ObjectMeta
            {
                Name = "kuberkynesis-lab-control-plane"
            },
            Spec = new V1NodeSpec
            {
                Unschedulable = false
            }
        };

        var pods = Enumerable.Range(0, 4)
            .Select(index => new V1Pod
            {
                Metadata = new V1ObjectMeta
                {
                    Name = $"orders-api-{index}",
                    NamespaceProperty = "orders-prod"
                },
                Status = new V1PodStatus
                {
                    Phase = "Running"
                }
            })
            .Concat(Enumerable.Range(0, 3)
                .Select(index => new V1Pod
                {
                    Metadata = new V1ObjectMeta
                    {
                        Name = $"payments-api-{index}",
                        NamespaceProperty = "payments-prod"
                    },
                    Status = new V1PodStatus
                    {
                        Phase = "Running"
                    }
                }))
            .Concat(
            [
                new V1Pod
                {
                    Metadata = new V1ObjectMeta
                    {
                        Name = "kube-proxy-control-plane",
                        NamespaceProperty = "kube-system"
                    },
                    Status = new V1PodStatus
                    {
                        Phase = "Running"
                    }
                }
            ])
            .ToArray();

        var preview = KubeActionPreviewFactory.CreateNodeSchedulingPreview(
            "kind-kuberkynesis-lab",
            node,
            pods,
            cordon: true);

        Assert.Equal(KubeActionKind.CordonNode, preview.Action);
        Assert.Equal(KubeActionRiskLevel.High, preview.Guardrails.RiskLevel);
        Assert.Contains(preview.Guardrails.Reasons, reason => reason.Contains("8 targets", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(preview.Guardrails.Reasons, reason => reason.Contains("3 namespaces", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(preview.Guardrails.Reasons, reason => reason.Contains("System namespaces", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CreateDrainNodePreview_WithMatchedDisruptionBudgets_ShowsBudgetScope()
    {
        var node = new V1Node
        {
            Metadata = new V1ObjectMeta
            {
                Name = "kuberkynesis-lab-worker"
            }
        };

        V1Pod[] pods =
        [
            new V1Pod
            {
                Metadata = new V1ObjectMeta
                {
                    Name = "orders-api-0",
                    NamespaceProperty = "orders-prod",
                    Labels = new Dictionary<string, string>
                    {
                        ["app.kubernetes.io/name"] = "orders-api"
                    }
                },
                Status = new V1PodStatus
                {
                    Phase = "Running"
                }
            },
            new V1Pod
            {
                Metadata = new V1ObjectMeta
                {
                    Name = "kube-proxy-worker",
                    NamespaceProperty = "kube-system"
                },
                Status = new V1PodStatus
                {
                    Phase = "Running"
                }
            }
        ];

        var disruptionBudgetImpact = KubePodDisruptionBudgetMatcher.BuildImpact(
            [
                new V1PodDisruptionBudget
                {
                    ApiVersion = "policy/v1",
                    Metadata = new V1ObjectMeta
                    {
                        Name = "orders-api-pdb",
                        NamespaceProperty = "orders-prod"
                    },
                    Spec = new V1PodDisruptionBudgetSpec
                    {
                        Selector = new V1LabelSelector
                        {
                            MatchLabels = new Dictionary<string, string>
                            {
                                ["app.kubernetes.io/name"] = "orders-api"
                            }
                        }
                    },
                    Status = new V1PodDisruptionBudgetStatus
                    {
                        DisruptionsAllowed = 0,
                        CurrentHealthy = 2,
                        DesiredHealthy = 2
                    }
                }
            ],
            pods);

        var preview = KubeActionPreviewFactory.CreateDrainNodePreview(
            "kind-kuberkynesis-lab",
            node,
            pods,
            disruptionBudgetImpact);

        Assert.Contains(preview.Facts, fact => fact.Label == "Matched PDBs" && fact.Value == "1");
        Assert.Contains(preview.Facts, fact => fact.Label == "Scope boundary" && fact.Value == "Cluster-scoped");
        Assert.Contains(preview.Facts, fact => fact.Label == "Affected namespaces" &&
                                               fact.Value.Contains("2 namespaces", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(preview.Warnings, warning => warning.Contains("limit or block eviction", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(preview.Warnings, warning => warning.Contains("spans 2 namespaces", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(preview.Warnings, warning => warning.Contains("System namespaces", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(preview.AffectedResources, resource => resource.Relationship == "Matched PDB" && resource.Name == "orders-api-pdb");
    }
}
