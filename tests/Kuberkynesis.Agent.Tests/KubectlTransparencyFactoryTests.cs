using Kuberkynesis.Agent.Kube;
using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Tests;

public sealed class KubectlTransparencyFactoryTests
{
    [Fact]
    public void CreateForQuery_BuildsContextAwareGetCommandsAndNotes()
    {
        var commands = KubectlTransparencyFactory.CreateForQuery(
            new KubeResourceQueryRequest
            {
                Kind = KubeResourceKind.Pod,
                Namespace = "orders-prod",
                Search = "orders-api"
            },
            ["kind-kuberkynesis-lab"],
            limitApplied: 100);

        var command = Assert.Single(commands);
        Assert.Equal("Resource query", command.Label);
        Assert.Equal("kubectl --context kind-kuberkynesis-lab -n orders-prod get pods -o wide", command.Command);
        Assert.Equal(KubectlTransparencyKind.Equivalent, command.TransparencyKind);
        Assert.Equal("Pod query", command.TargetSummary);
        Assert.Equal("kind-kuberkynesis-lab / orders-prod", command.ScopeSummary);
        Assert.Contains("Search 'orders-api' is applied in the UI after retrieval.", command.Notes, StringComparison.Ordinal);
        Assert.Contains("UI result cap: 100.", command.Notes, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateForQuery_MixedKindModeBuildsClusterAndNamespacedCommands()
    {
        var commands = KubectlTransparencyFactory.CreateForQuery(
            new KubeResourceQueryRequest
            {
                Kind = KubeResourceKind.Pod,
                IncludeAllSupportedKinds = true,
                Search = "checkout"
            },
            ["kind-kuberkynesis-lab"],
            limitApplied: 100);

        Assert.Collection(commands,
            command =>
            {
                Assert.Equal("Cluster-scoped kinds", command.Label);
                Assert.Equal("kubectl --context kind-kuberkynesis-lab get namespaces,nodes -o wide", command.Command);
                Assert.Equal(KubectlTransparencyKind.Approximate, command.TransparencyKind);
                Assert.Equal("All supported cluster kinds", command.TargetSummary);
                Assert.Equal("kind-kuberkynesis-lab / cluster-scoped", command.ScopeSummary);
                Assert.Contains("All supported kinds fans out across typed resource queries", command.Notes, StringComparison.Ordinal);
            },
            command =>
            {
                Assert.Equal("Namespaced kinds", command.Label);
                Assert.Equal("kubectl --context kind-kuberkynesis-lab -A get pods,deployments,replicasets,statefulsets,daemonsets,services,ingresses,configmaps,secrets,jobs,cronjobs,events -o wide", command.Command);
                Assert.Equal(KubectlTransparencyKind.Approximate, command.TransparencyKind);
                Assert.Equal("All supported namespaced kinds", command.TargetSummary);
                Assert.Equal("kind-kuberkynesis-lab / all namespaces", command.ScopeSummary);
                Assert.Contains("Search 'checkout' is applied in the UI after retrieval.", command.Notes, StringComparison.Ordinal);
            });
    }

    [Fact]
    public void CreateForDetail_UsesDescribeAndKeepsSecretOutputMetadataSafe()
    {
        var commands = KubectlTransparencyFactory.CreateForDetail(
            new KubeResourceDetailRequest
            {
                ContextName = "kind-kuberkynesis-lab",
                Kind = KubeResourceKind.Secret,
                Namespace = "orders-prod",
                Name = "orders-api"
            });

        Assert.Collection(commands,
            command =>
            {
                Assert.Equal("kubectl --context kind-kuberkynesis-lab -n orders-prod describe secrets orders-api", command.Command);
                Assert.Equal("Secret/orders-api", command.TargetSummary);
                Assert.Equal("kind-kuberkynesis-lab / orders-prod", command.ScopeSummary);
                Assert.Contains("Secret values stay hidden", command.Notes, StringComparison.Ordinal);
                Assert.Contains("no reveal path", command.Notes, StringComparison.Ordinal);
            },
            command =>
            {
                Assert.Equal("kubectl --context kind-kuberkynesis-lab -n orders-prod get secrets orders-api -o json", command.Command);
                Assert.Contains("UI redacts them", command.Notes, StringComparison.Ordinal);
                Assert.Contains("does not provide a reveal toggle", command.Notes, StringComparison.Ordinal);
            });
    }

    [Fact]
    public void CreateForDetail_UsesReplicaSetResourceName()
    {
        var commands = KubectlTransparencyFactory.CreateForDetail(
            new KubeResourceDetailRequest
            {
                ContextName = "kind-kuberkynesis-lab",
                Kind = KubeResourceKind.ReplicaSet,
                Namespace = "orders-prod",
                Name = "orders-api-5d4566bdf6"
            });

        Assert.Equal(
            "kubectl --context kind-kuberkynesis-lab -n orders-prod describe replicasets orders-api-5d4566bdf6",
            commands[0].Command);
        Assert.Equal(
            "kubectl --context kind-kuberkynesis-lab -n orders-prod get replicasets orders-api-5d4566bdf6 -o json",
            commands[1].Command);
    }

    [Fact]
    public void CreateForGraph_ExplainsThatTheGraphIsInferred()
    {
        var commands = KubectlTransparencyFactory.CreateForGraph(
            new KubeResourceDetailRequest
            {
                ContextName = "kind-kuberkynesis-lab",
                Kind = KubeResourceKind.Pod,
                Namespace = "orders-prod",
                Name = "orders-api-abc123"
            });

        Assert.Collection(commands,
            command =>
            {
                Assert.Equal("kubectl --context kind-kuberkynesis-lab -n orders-prod describe pods orders-api-abc123", command.Command);
                Assert.Contains("dependency graph starts from this resource", command.Notes, StringComparison.OrdinalIgnoreCase);
            },
            command =>
            {
                Assert.Equal("kubectl --context kind-kuberkynesis-lab -n orders-prod get pods orders-api-abc123 -o json", command.Command);
                Assert.Contains("no native dependency graph output", command.Notes, StringComparison.OrdinalIgnoreCase);
            });
    }

    [Fact]
    public void CreateForTimeline_UsesEventFieldSelectors()
    {
        var commands = KubectlTransparencyFactory.CreateForTimeline(
            new KubeResourceDetailRequest
            {
                ContextName = "kind-kuberkynesis-lab",
                Kind = KubeResourceKind.Pod,
                Namespace = "orders-prod",
                Name = "orders-api-abc123"
            });

        var command = Assert.Single(commands);
        Assert.Equal(
            "kubectl --context kind-kuberkynesis-lab -n orders-prod get events --field-selector involvedObject.kind=Pod,involvedObject.name=orders-api-abc123 --sort-by=.lastTimestamp",
            command.Command);
        Assert.Contains("nearby related-resource events", command.Notes, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateForDetail_UsesEventsResourceNameForEvent()
    {
        var commands = KubectlTransparencyFactory.CreateForDetail(
            new KubeResourceDetailRequest
            {
                ContextName = "kind-kuberkynesis-lab",
                Kind = KubeResourceKind.Event,
                Namespace = "orders-prod",
                Name = "orders-api-abc123.182f8d4c0f9ad6e1"
            });

        Assert.Equal(
            "kubectl --context kind-kuberkynesis-lab -n orders-prod describe events orders-api-abc123.182f8d4c0f9ad6e1",
            commands[0].Command);
        Assert.Equal(
            "kubectl --context kind-kuberkynesis-lab -n orders-prod get events orders-api-abc123.182f8d4c0f9ad6e1 -o json",
            commands[1].Command);
    }

    [Fact]
    public void CreateForPodLogs_IncludesContainerAndTailFlags()
    {
        var commands = KubectlTransparencyFactory.CreateForPodLogs(
            new KubePodLogRequest
            {
                ContextName = "kind-kuberkynesis-lab",
                Namespace = "orders-prod",
                PodName = "orders-api-abc123",
                TailLines = 200
            },
            resolvedContainerName: "api",
            containerWasAutoSelected: true);

        var command = Assert.Single(commands);
        Assert.Equal(
            "kubectl --context kind-kuberkynesis-lab -n orders-prod logs pod/orders-api-abc123 -c api --tail=200 --timestamps",
            command.Command);
        Assert.Equal(KubectlTransparencyKind.Equivalent, command.TransparencyKind);
        Assert.Equal("Pod/orders-api-abc123", command.TargetSummary);
        Assert.Equal("kind-kuberkynesis-lab / orders-prod", command.ScopeSummary);
        Assert.Contains("auto-selected", command.Notes, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateForActionPreview_UsesScaleDeploymentCommand()
    {
        var commands = KubectlTransparencyFactory.CreateForActionPreview(
            new KubeActionPreviewRequest(
                ContextName: "kind-kuberkynesis-lab",
                Kind: KubeResourceKind.Deployment,
                Namespace: "orders-prod",
                Name: "orders-api",
                Action: KubeActionKind.ScaleDeployment,
                TargetReplicas: 5));

        var command = Assert.Single(commands);
        Assert.Equal(
            "kubectl --context kind-kuberkynesis-lab -n orders-prod scale deployment/orders-api --replicas=5",
            command.Command);
        Assert.True(command.IsDryRun);
        Assert.Equal("replicas=5", command.RequestSummary);
        Assert.Equal("Deployment/orders-api", command.TargetSummary);
        Assert.Contains("does not execute", command.Notes, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateForActionPreview_UsesStatefulSetScaleCommand()
    {
        var commands = KubectlTransparencyFactory.CreateForActionPreview(
            new KubeActionPreviewRequest(
                ContextName: "kind-kuberkynesis-lab",
                Kind: KubeResourceKind.StatefulSet,
                Namespace: "orders-prod",
                Name: "orders-db",
                Action: KubeActionKind.ScaleStatefulSet,
                TargetReplicas: 2));

        var command = Assert.Single(commands);
        Assert.Equal(
            "kubectl --context kind-kuberkynesis-lab -n orders-prod scale statefulset/orders-db --replicas=2",
            command.Command);
    }

    [Fact]
    public void CreateForActionPreview_UsesRolloutUndoCommand()
    {
        var commands = KubectlTransparencyFactory.CreateForActionPreview(
            new KubeActionPreviewRequest(
                ContextName: "kind-kuberkynesis-lab",
                Kind: KubeResourceKind.Deployment,
                Namespace: "orders-prod",
                Name: "orders-api",
                Action: KubeActionKind.RollbackDeploymentRollout));

        var command = Assert.Single(commands);
        Assert.Equal(
            "kubectl --context kind-kuberkynesis-lab -n orders-prod rollout undo deployment/orders-api",
            command.Command);
        Assert.True(command.IsDryRun);
        Assert.Equal("rollbackToRevision=<retained prior revision>", command.RequestSummary);
    }

    [Fact]
    public void CreateForActionPreview_UsesDrainPreviewCommand()
    {
        var commands = KubectlTransparencyFactory.CreateForActionPreview(
            new KubeActionPreviewRequest(
                ContextName: "kind-kuberkynesis-lab",
                Kind: KubeResourceKind.Node,
                Namespace: null,
                Name: "kuberkynesis-lab-worker",
                Action: KubeActionKind.DrainNode));

        var command = Assert.Single(commands);
        Assert.Equal(
            "kubectl --context kind-kuberkynesis-lab drain kuberkynesis-lab-worker --ignore-daemonsets --delete-emptydir-data",
            command.Command);
        Assert.Equal(KubectlTransparencyKind.Approximate, command.TransparencyKind);
        Assert.True(command.IsDryRun);
        Assert.Equal("Node/kuberkynesis-lab-worker", command.TargetSummary);
        Assert.Equal("kind-kuberkynesis-lab / cluster-scoped", command.ScopeSummary);
        Assert.Contains("read-only", command.Notes, StringComparison.OrdinalIgnoreCase);
    }
}
