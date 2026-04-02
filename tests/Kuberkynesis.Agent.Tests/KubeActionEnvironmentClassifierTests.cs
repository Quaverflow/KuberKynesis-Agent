using Kuberkynesis.Agent.Kube;
using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Tests;

public sealed class KubeActionEnvironmentClassifierTests
{
    [Fact]
    public void Classify_UsesLocalRulesWhenBuiltInSignalsAreMissing()
    {
        var environment = KubeActionEnvironmentClassifier.Classify(
            contextName: "kind-kuberkynesis-lab",
            namespaceName: "orders-live",
            labels: null,
            annotations: null,
            localRules: new KubeActionLocalEnvironmentRules(
                ProductionMatchers: ["orders-live"],
                StagingMatchers: [],
                DevelopmentMatchers: []));

        Assert.Equal(KubeActionEnvironmentKind.Production, environment);
    }

    [Fact]
    public void Classify_PrefersBuiltInSignalsBeforeLocalRules()
    {
        var environment = KubeActionEnvironmentClassifier.Classify(
            contextName: "kind-kuberkynesis-lab",
            namespaceName: "orders-dev",
            labels: null,
            annotations: null,
            localRules: new KubeActionLocalEnvironmentRules(
                ProductionMatchers: ["orders-dev"],
                StagingMatchers: [],
                DevelopmentMatchers: []));

        Assert.Equal(KubeActionEnvironmentKind.Development, environment);
    }

    [Fact]
    public void Classify_UsesContextMatchersForClusterScopedTargets()
    {
        var environment = KubeActionEnvironmentClassifier.Classify(
            contextName: "prod-eu-cluster",
            namespaceName: null,
            labels: null,
            annotations: null,
            localRules: new KubeActionLocalEnvironmentRules(
                ProductionMatchers: ["prod-eu"],
                StagingMatchers: [],
                DevelopmentMatchers: []));

        Assert.Equal(KubeActionEnvironmentKind.Production, environment);
    }
}
