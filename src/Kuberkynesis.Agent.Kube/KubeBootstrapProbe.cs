namespace Kuberkynesis.Agent.Kube;

public sealed record KubeBootstrapProbeResult(
    bool KubeConfigAvailable,
    bool KubectlAvailable,
    string? KubectlClientVersion,
    string? CurrentContextName,
    int ContextCount,
    IReadOnlyList<string> SourcePaths,
    IReadOnlyList<string> Warnings);

public sealed class KubeBootstrapProbe
{
    private readonly IKubeConfigLoader kubeConfigLoader;
    private readonly IKubectlAvailabilityProbe kubectlAvailabilityProbe;

    public KubeBootstrapProbe(IKubeConfigLoader kubeConfigLoader, IKubectlAvailabilityProbe kubectlAvailabilityProbe)
    {
        this.kubeConfigLoader = kubeConfigLoader;
        this.kubectlAvailabilityProbe = kubectlAvailabilityProbe;
    }

    public KubeBootstrapProbeResult Probe()
    {
        var loadResult = kubeConfigLoader.Load();
        var kubectlResult = kubectlAvailabilityProbe.Probe();
        var warnings = loadResult.Warnings.ToList();

        if (!kubectlResult.IsAvailable && !string.IsNullOrWhiteSpace(kubectlResult.Warning))
        {
            warnings.Add(kubectlResult.Warning);
        }

        return new KubeBootstrapProbeResult(
            KubeConfigAvailable: loadResult.Configuration is not null,
            KubectlAvailable: kubectlResult.IsAvailable,
            KubectlClientVersion: kubectlResult.ClientVersion,
            CurrentContextName: loadResult.CurrentContextName,
            ContextCount: loadResult.Contexts.Count,
            SourcePaths: loadResult.SourcePaths.Select(path => path.FullName).ToArray(),
            Warnings: warnings);
    }
}
