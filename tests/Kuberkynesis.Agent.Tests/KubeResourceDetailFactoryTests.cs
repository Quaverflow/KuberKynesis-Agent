using k8s.Models;
using Kuberkynesis.Agent.Kube;
using Kuberkynesis.Ui.Shared.Kubernetes;
using System.Text.Json.Nodes;

namespace Kuberkynesis.Agent.Tests;

public sealed class KubeResourceDetailFactoryTests
{
    [Fact]
    public void Create_ForPod_IncludesContainerFactsAndOwnerRelations()
    {
        var pod = new V1Pod
        {
            ApiVersion = "v1",
            Metadata = new V1ObjectMeta
            {
                Name = "orders-api-abc123",
                NamespaceProperty = "orders-prod",
                Uid = "pod-01",
                Labels = new Dictionary<string, string>
                {
                    ["app.kubernetes.io/name"] = "orders-api"
                },
                OwnerReferences =
                [
                    new V1OwnerReference
                    {
                        ApiVersion = "apps/v1",
                        Kind = "ReplicaSet",
                        Name = "orders-api-5d4566bdf6",
                        Uid = "rs-01",
                        Controller = true
                    }
                ]
            },
            Spec = new V1PodSpec
            {
                NodeName = "worker-a",
                ServiceAccountName = "orders-api",
                SchedulerName = "default-scheduler",
                PriorityClassName = "production-critical",
                HostNetwork = false,
                DnsPolicy = "ClusterFirst",
                NodeSelector = new Dictionary<string, string>
                {
                    ["kubernetes.io/os"] = "linux"
                },
                Tolerations =
                [
                    new V1Toleration
                    {
                        Key = "dedicated",
                        OperatorProperty = "Equal",
                        Value = "orders",
                        Effect = "NoSchedule"
                    }
                ],
                TopologySpreadConstraints =
                [
                    new V1TopologySpreadConstraint
                    {
                        TopologyKey = "topology.kubernetes.io/zone",
                        MaxSkew = 1,
                        WhenUnsatisfiable = "ScheduleAnyway"
                    }
                ],
                Containers =
                [
                    new V1Container
                    {
                        Name = "api",
                        Image = "ghcr.io/kuberkynesis/orders-api:1.2.3",
                        Command =
                        [
                            "/app/orders-api"
                        ],
                        Args =
                        [
                            "--serve"
                        ],
                        Ports =
                        [
                            new V1ContainerPort
                            {
                                Name = "http",
                                ContainerPort = 8080,
                                Protocol = "TCP"
                            }
                        ],
                        EnvFrom =
                        [
                            new V1EnvFromSource
                            {
                                ConfigMapRef = new V1ConfigMapEnvSource
                                {
                                    Name = "orders-shared-config"
                                }
                            },
                            new V1EnvFromSource
                            {
                                SecretRef = new V1SecretEnvSource
                                {
                                    Name = "orders-api-secrets"
                                }
                            }
                        ],
                        Env =
                        [
                            new V1EnvVar
                            {
                                Name = "POD_NAME",
                                ValueFrom = new V1EnvVarSource
                                {
                                    FieldRef = new V1ObjectFieldSelector
                                    {
                                        FieldPath = "metadata.name"
                                    }
                                }
                            }
                        ],
                        Resources = new V1ResourceRequirements
                        {
                            Requests = new Dictionary<string, ResourceQuantity>
                            {
                                ["cpu"] = new ResourceQuantity("100m")
                            },
                            Limits = new Dictionary<string, ResourceQuantity>
                            {
                                ["memory"] = new ResourceQuantity("256Mi")
                            }
                        },
                        ReadinessProbe = new V1Probe
                        {
                            HttpGet = new V1HTTPGetAction
                            {
                                Path = "/ready",
                                Port = 8080
                            },
                            PeriodSeconds = 5
                        },
                        VolumeMounts =
                        [
                            new V1VolumeMount
                            {
                                Name = "config",
                                MountPath = "/etc/config",
                                ReadOnlyProperty = true
                            }
                        ]
                    }
                ],
                InitContainers =
                [
                    new V1Container
                    {
                        Name = "migrate",
                        Image = "ghcr.io/kuberkynesis/orders-api-migrate:1.2.3",
                        Command =
                        [
                            "/app/migrate"
                        ]
                    }
                ],
                Volumes =
                [
                    new V1Volume
                    {
                        Name = "config",
                        ConfigMap = new V1ConfigMapVolumeSource
                        {
                            Name = "orders-shared-config"
                        }
                    }
                ]
            },
            Status = new V1PodStatus
            {
                Phase = "Running",
                PodIP = "10.244.1.12",
                HostIP = "172.18.0.2",
                QosClass = "Burstable",
                Reason = "Running",
                ContainerStatuses =
                [
                    new V1ContainerStatus
                    {
                        Name = "api",
                        Image = "ghcr.io/kuberkynesis/orders-api:1.2.3",
                        ImageID = "img-01",
                        Ready = true,
                        RestartCount = 2,
                        State = new V1ContainerState
                        {
                            Running = new V1ContainerStateRunning()
                        },
                        LastState = new V1ContainerState
                        {
                            Terminated = new V1ContainerStateTerminated
                            {
                                Reason = "Error"
                            }
                        }
                    }
                ],
                InitContainerStatuses =
                [
                    new V1ContainerStatus
                    {
                        Name = "migrate",
                        Ready = true,
                        RestartCount = 0,
                        State = new V1ContainerState
                        {
                            Terminated = new V1ContainerStateTerminated
                            {
                                Reason = "Completed"
                            }
                        }
                    }
                ],
                Conditions =
                [
                    new V1PodCondition
                    {
                        Type = "Ready",
                        Status = "True",
                        Reason = "ContainersReady",
                        Message = "All containers are ready"
                    },
                    new V1PodCondition
                    {
                        Type = "PodScheduled",
                        Status = "True",
                        Reason = "Scheduled",
                        Message = "Assigned to worker-a"
                    }
                ]
            }
        };

        var detail = KubeResourceDetailFactory.Create("kind-kuberkynesis-lab", pod);

        Assert.Equal("orders-api-abc123", detail.Resource.Name);

        var containers = Assert.Single(detail.Sections, section => section.Title == "Containers");
        var containerField = Assert.Single(containers.Fields);
        Assert.Equal("api", containerField.Label);
        Assert.Contains("orders-api:1.2.3", containerField.Value, StringComparison.Ordinal);

        var containerStatus = Assert.Single(detail.Sections, section => section.Title == "Container status");
        Assert.Contains(containerStatus.Fields, field => field.Label == "api" && field.Value.Contains("running", StringComparison.Ordinal));
        Assert.Contains(containerStatus.Fields, field => field.Label == "api" && field.Value.Contains("restarts 2", StringComparison.Ordinal));

        var declaredPorts = Assert.Single(detail.Sections, section => section.Title == "Declared ports");
        Assert.Contains(declaredPorts.Fields, field => field.Label == "api / http" && field.Value == "8080/TCP");

        var environmentSources = Assert.Single(detail.Sections, section => section.Title == "Environment sources");
        Assert.Contains(environmentSources.Fields, field => field.Label == "api / ConfigMap" && field.Value == "orders-shared-config");
        Assert.Contains(environmentSources.Fields, field => field.Label == "api / Secret" && field.Value == "orders-api-secrets");
        Assert.Contains(environmentSources.Fields, field => field.Label == "api / env POD_NAME" && field.Value == "fieldRef metadata.name");

        var commands = Assert.Single(detail.Sections, section => section.Title == "Container commands");
        Assert.Contains(commands.Fields, field => field.Label == "api" && field.Value == "/app/orders-api --serve");
        Assert.Contains(commands.Fields, field => field.Label == "migrate (init)" && field.Value == "/app/migrate");

        var resources = Assert.Single(detail.Sections, section => section.Title == "Container resources");
        Assert.Contains(resources.Fields, field => field.Label == "api / requests" && field.Value.Contains("cpu=100m", StringComparison.Ordinal));
        Assert.Contains(resources.Fields, field => field.Label == "api / limits" && field.Value.Contains("memory=256Mi", StringComparison.Ordinal));

        var probes = Assert.Single(detail.Sections, section => section.Title == "Container probes");
        Assert.Contains(probes.Fields, field => field.Label == "api / readiness" && field.Value.Contains("http GET /ready on 8080", StringComparison.Ordinal));

        var volumeMounts = Assert.Single(detail.Sections, section => section.Title == "Volume mounts");
        Assert.Contains(volumeMounts.Fields, field => field.Label == "api / /etc/config" && field.Value.Contains("config", StringComparison.Ordinal));

        var volumes = Assert.Single(detail.Sections, section => section.Title == "Volumes");
        Assert.Contains(volumes.Fields, field => field.Label == "config" && field.Value == "ConfigMap/orders-shared-config");

        var overview = Assert.Single(detail.Sections, section => section.Title == "Overview");
        Assert.Contains(overview.Fields, field => field.Label == "Priority class" && field.Value == "production-critical");

        var scheduling = Assert.Single(detail.Sections, section => section.Title == "Scheduling");
        Assert.Contains(scheduling.Fields, field => field.Label == "Scheduler" && field.Value == "default-scheduler");
        Assert.Contains(scheduling.Fields, field => field.Label == "Node selectors" && field.Value == "1");

        var tolerations = Assert.Single(detail.Sections, section => section.Title == "Tolerations");
        Assert.Contains(tolerations.Fields, field => field.Label == "dedicated" && field.Value.Contains("NoSchedule", StringComparison.Ordinal));

        var affinity = Assert.Single(detail.Sections, section => section.Title == "Affinity and spread");
        Assert.Contains(affinity.Fields, field => field.Label == "Node selector" && field.Value.Contains("kubernetes.io/os=linux", StringComparison.Ordinal));
        Assert.Contains(affinity.Fields, field => field.Label == "Topology spread / topology.kubernetes.io/zone" && field.Value.Contains("max skew 1", StringComparison.Ordinal));

        var lifecycle = Assert.Single(detail.Sections, section => section.Title == "Health and lifecycle");
        Assert.Contains(lifecycle.Fields, field => field.Label == "Ready containers" && field.Value == "1/1");
        Assert.Contains(lifecycle.Fields, field => field.Label == "Init containers ready" && field.Value == "1/1");
        Assert.Contains(lifecycle.Fields, field => field.Label == "Last termination" && field.Value.Contains("Error", StringComparison.Ordinal));

        Assert.Contains(detail.RelatedResources, relation =>
            relation.Relationship == "Owned by" &&
            relation.Kind == KubeResourceKind.ReplicaSet &&
            relation.Name == "orders-api-5d4566bdf6");

        Assert.Contains(detail.RelatedResources, relation =>
            relation.Relationship == "Scheduled on" &&
            relation.Kind == KubeResourceKind.Node &&
            relation.Name == "worker-a");

        Assert.Contains(detail.RelatedResources, relation =>
            relation.Relationship == "Uses ConfigMap" &&
            relation.Kind == KubeResourceKind.ConfigMap &&
            relation.Name == "orders-shared-config" &&
            relation.Summary == "environment, volume");

        Assert.Contains(detail.RelatedResources, relation =>
            relation.Relationship == "Uses Secret" &&
            relation.Kind == KubeResourceKind.Secret &&
            relation.Name == "orders-api-secrets" &&
            relation.Summary == "environment");
    }

    [Fact]
    public void Create_ForService_IncludesPortsSelectorsAndSelectedPods()
    {
        var service = new V1Service
        {
            ApiVersion = "v1",
            Metadata = new V1ObjectMeta
            {
                Name = "orders-api",
                NamespaceProperty = "orders-prod"
            },
            Spec = new V1ServiceSpec
            {
                Type = "ClusterIP",
                ClusterIP = "10.96.41.12",
                Selector = new Dictionary<string, string>
                {
                    ["app.kubernetes.io/name"] = "orders-api"
                },
                Ports =
                [
                    new V1ServicePort
                    {
                        Port = 8080,
                        Protocol = "TCP",
                        Name = "http"
                    }
                ]
            }
        };

        var detail = KubeResourceDetailFactory.Create(
            "kind-kuberkynesis-lab",
            service,
            relatedResources:
            [
                new KubeRelatedResource(
                    Relationship: "Selected pod",
                    Kind: KubeResourceKind.Pod,
                    ApiVersion: "v1",
                    Name: "orders-api-abc123",
                    Namespace: "orders-prod",
                    Status: "Running",
                    Summary: "1/1 ready")
            ]);

        var selectors = Assert.Single(detail.Sections, section => section.Title == "Selector");
        Assert.Contains(selectors.Fields, field => field.Label == "app.kubernetes.io/name" && field.Value == "orders-api");

        var ports = Assert.Single(detail.Sections, section => section.Title == "Ports");
        Assert.Contains(ports.Fields, field => field.Label == "http" && field.Value == "8080/TCP");

        Assert.Contains(detail.RelatedResources, relation =>
            relation.Relationship == "Selected pod" &&
            relation.Name == "orders-api-abc123");
    }

    [Fact]
    public void Create_ForReplicaSet_IncludesSelectorContainersAndConditions()
    {
        var replicaSet = new V1ReplicaSet
        {
            ApiVersion = "apps/v1",
            Metadata = new V1ObjectMeta
            {
                Name = "orders-api-5d4566bdf6",
                NamespaceProperty = "orders-prod"
            },
            Spec = new V1ReplicaSetSpec
            {
                Replicas = 3,
                MinReadySeconds = 10,
                Selector = new V1LabelSelector
                {
                    MatchLabels = new Dictionary<string, string>
                    {
                        ["app.kubernetes.io/name"] = "orders-api"
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
                                Name = "api",
                                Image = "ghcr.io/kuberkynesis/orders-api:1.2.3"
                            }
                        ]
                    }
                }
            },
            Status = new V1ReplicaSetStatus
            {
                ReadyReplicas = 2,
                Conditions =
                [
                    new V1ReplicaSetCondition
                    {
                        Type = "ReplicaFailure",
                        Status = "False",
                        Reason = "AsExpected",
                        Message = "Pods are progressing"
                    }
                ]
            }
        };

        var detail = KubeResourceDetailFactory.Create("kind-kuberkynesis-lab", replicaSet);

        Assert.Equal(KubeResourceKind.ReplicaSet, detail.Resource.Kind);

        var overview = Assert.Single(detail.Sections, section => section.Title == "Overview");
        Assert.Contains(overview.Fields, field => field.Label == "Replicas" && field.Value == "2/3 ready");
        Assert.Contains(overview.Fields, field => field.Label == "Min ready seconds" && field.Value == "10");

        var selector = Assert.Single(detail.Sections, section => section.Title == "Selector");
        Assert.Contains(selector.Fields, field => field.Label == "app.kubernetes.io/name" && field.Value == "orders-api");

        var containers = Assert.Single(detail.Sections, section => section.Title == "Containers");
        Assert.Contains(containers.Fields, field => field.Label == "api" && field.Value.Contains("orders-api:1.2.3", StringComparison.Ordinal));

        var conditions = Assert.Single(detail.Sections, section => section.Title == "Conditions");
        Assert.Contains(conditions.Fields, field => field.Label == "ReplicaFailure" && field.Value.Contains("AsExpected", StringComparison.Ordinal));
    }

    [Fact]
    public void Create_ForDeployment_AddsConfigMapAndSecretDependencyRelationsFromTemplateSpec()
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
                Selector = new V1LabelSelector
                {
                    MatchLabels = new Dictionary<string, string>
                    {
                        ["app.kubernetes.io/name"] = "orders-api"
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
                                Name = "api",
                                Image = "ghcr.io/kuberkynesis/orders-api:1.2.3",
                                Env =
                                [
                                    new V1EnvVar
                                    {
                                        Name = "DB_HOST",
                                        ValueFrom = new V1EnvVarSource
                                        {
                                            ConfigMapKeyRef = new V1ConfigMapKeySelector
                                            {
                                                Name = "orders-runtime-config",
                                                Key = "DB_HOST"
                                            }
                                        }
                                    }
                                ]
                            }
                        ],
                        Volumes =
                        [
                            new V1Volume
                            {
                                Name = "api-secrets",
                                Secret = new V1SecretVolumeSource
                                {
                                    SecretName = "orders-api-secrets"
                                }
                            }
                        ],
                        ImagePullSecrets =
                        [
                            new V1LocalObjectReference
                            {
                                Name = "registry-pull"
                            }
                        ]
                    }
                }
            }
        };

        var detail = KubeResourceDetailFactory.Create("kind-kuberkynesis-lab", deployment);

        Assert.Contains(detail.RelatedResources, relation =>
            relation.Relationship == "Uses ConfigMap" &&
            relation.Kind == KubeResourceKind.ConfigMap &&
            relation.Name == "orders-runtime-config" &&
            relation.Summary == "environment");

        Assert.Contains(detail.RelatedResources, relation =>
            relation.Relationship == "Uses Secret" &&
            relation.Kind == KubeResourceKind.Secret &&
            relation.Name == "orders-api-secrets" &&
            relation.Summary == "volume");

        Assert.Contains(detail.RelatedResources, relation =>
            relation.Relationship == "Uses Secret" &&
            relation.Kind == KubeResourceKind.Secret &&
            relation.Name == "registry-pull" &&
            relation.Summary == "image pull");
    }

    [Fact]
    public void Create_ForSecret_RedactsValuesInRawJson()
    {
        var secret = new V1Secret
        {
            ApiVersion = "v1",
            Metadata = new V1ObjectMeta
            {
                Name = "orders-api-secrets",
                NamespaceProperty = "orders-prod",
                Annotations = new Dictionary<string, string>
                {
                    ["kubectl.kubernetes.io/last-applied-configuration"] =
                        """
                        {"apiVersion":"v1","kind":"Secret","metadata":{"name":"orders-api-secrets","namespace":"orders-prod"},"stringData":{"API_KEY":"very-secret"},"data":{"DB_PASSWORD":"dmVyeS1zZWNyZXQ="}}
                        """
                }
            },
            Type = "Opaque",
            Data = new Dictionary<string, byte[]>
            {
                ["DB_PASSWORD"] = [1, 2, 3]
            },
            StringData = new Dictionary<string, string>
            {
                ["API_KEY"] = "very-secret"
            }
        };

        var detail = KubeResourceDetailFactory.Create("kind-kuberkynesis-lab", secret);
        var rawJson = JsonNode.Parse(detail.RawJson!);

        Assert.NotNull(detail.RawJson);
        Assert.Equal("<redacted>", rawJson?["data"]?["DB_PASSWORD"]?.GetValue<string>());
        Assert.Equal("<redacted>", rawJson?["stringData"]?["API_KEY"]?.GetValue<string>());
        var lastAppliedConfiguration = rawJson?["metadata"]?["annotations"]?["kubectl.kubernetes.io/last-applied-configuration"]?.GetValue<string>();
        Assert.NotNull(lastAppliedConfiguration);
        var redactedLastAppliedConfiguration = JsonNode.Parse(lastAppliedConfiguration!);
        Assert.Equal("<redacted>", redactedLastAppliedConfiguration?["data"]?["DB_PASSWORD"]?.GetValue<string>());
        Assert.Equal("<redacted>", redactedLastAppliedConfiguration?["stringData"]?["API_KEY"]?.GetValue<string>());
        Assert.DoesNotContain("very-secret", detail.RawJson, StringComparison.Ordinal);
        Assert.DoesNotContain("dmVyeS1zZWNyZXQ=", detail.RawJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_ForEvent_IncludesInvolvedObjectRelationAndMessage()
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
            Message = "Readiness probe failed",
            Count = 2,
            InvolvedObject = new V1ObjectReference
            {
                ApiVersion = "v1",
                Kind = "Pod",
                Name = "orders-api-abc123",
                NamespaceProperty = "orders-prod"
            }
        };

        var detail = KubeResourceDetailFactory.Create("kind-kuberkynesis-lab", item);

        Assert.Equal(KubeResourceKind.Event, detail.Resource.Kind);
        Assert.Contains(detail.Sections, section => section.Title == "Overview" && section.Fields.Any(field => field.Label == "Reason" && field.Value == "Unhealthy"));
        Assert.Contains(detail.Sections, section => section.Title == "Message" && section.Fields.Any(field => field.Label == "Details" && field.Value == "Readiness probe failed"));
        Assert.Contains(detail.RelatedResources, relation =>
            relation.Relationship == "Regarding" &&
            relation.Kind == KubeResourceKind.Pod &&
            relation.Name == "orders-api-abc123");
    }

    [Fact]
    public void Create_ForCustomResource_IncludesMetadataStatusAndRawYaml()
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
                ["uid"] = "widget-uid-01",
                ["creationTimestamp"] = "2026-03-29T12:00:00Z",
                ["ownerReferences"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["apiVersion"] = "apps/v1",
                        ["kind"] = "Deployment",
                        ["name"] = "checkout-api"
                    }
                }
            },
            ["spec"] = new JsonObject
            {
                ["tier"] = "checkout"
            },
            ["status"] = new JsonObject
            {
                ["phase"] = "Ready",
                ["url"] = "https://checkout.example"
            }
        };

        var detail = KubeResourceDetailFactory.Create("kind-kuberkynesis-lab", customResourceType, item);

        Assert.Equal(KubeResourceKind.CustomResource, detail.Resource.Kind);
        Assert.Equal("Widget", detail.Resource.CustomResourceType?.Kind);
        Assert.Contains(detail.Sections, section => section.Title == "Definition");
        Assert.Contains(detail.Sections, section => section.Title == "Status");
        Assert.Contains(detail.Sections, section => section.Title == "Spec");
        Assert.Contains(detail.RelatedResources, relation =>
            relation.Relationship == "Owned by" &&
            relation.Kind == KubeResourceKind.Deployment &&
            relation.Name == "checkout-api");
        Assert.Contains("kind: Widget", detail.RawYaml, StringComparison.Ordinal);
        Assert.Contains("\"kind\": \"Widget\"", detail.RawJson, StringComparison.Ordinal);
    }
}
