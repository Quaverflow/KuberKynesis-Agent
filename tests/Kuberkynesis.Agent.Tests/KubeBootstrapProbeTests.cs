using Kuberkynesis.Agent.Kube;
using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Tests;

public sealed class KubeBootstrapProbeTests
{
    [Fact]
    public void Probe_IncludesKubectlStatusAndWarnings()
    {
        var loadResult = new KubeConfigLoadResult(
            Configuration: null,
            SourcePaths: [],
            CurrentContextName: null,
            Contexts: [],
            Warnings:
            [
                "No kubeconfig file was found."
            ]);

        var probe = new KubeBootstrapProbe(
            new StubKubeConfigLoader(loadResult),
            new StubKubectlAvailabilityProbe(new KubectlAvailabilityProbeResult(
                IsAvailable: false,
                ClientVersion: null,
                Warning: "kubectl is not available: The system cannot find the file specified.")));

        var result = probe.Probe();

        Assert.False(result.KubeConfigAvailable);
        Assert.False(result.KubectlAvailable);
        Assert.Null(result.KubectlClientVersion);
        Assert.Contains("No kubeconfig file was found.", result.Warnings);
        Assert.Contains("kubectl is not available", string.Join(" | ", result.Warnings), StringComparison.OrdinalIgnoreCase);
    }

    private sealed class StubKubeConfigLoader : IKubeConfigLoader
    {
        private readonly KubeConfigLoadResult loadResult;

        public StubKubeConfigLoader(KubeConfigLoadResult loadResult)
        {
            this.loadResult = loadResult;
        }

        public KubeConfigLoadResult Load()
        {
            return loadResult;
        }

        public k8s.Kubernetes CreateClient(KubeConfigLoadResult loadResult, string contextName)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class StubKubectlAvailabilityProbe : IKubectlAvailabilityProbe
    {
        private readonly KubectlAvailabilityProbeResult result;

        public StubKubectlAvailabilityProbe(KubectlAvailabilityProbeResult result)
        {
            this.result = result;
        }

        public KubectlAvailabilityProbeResult Probe()
        {
            return result;
        }
    }
}
