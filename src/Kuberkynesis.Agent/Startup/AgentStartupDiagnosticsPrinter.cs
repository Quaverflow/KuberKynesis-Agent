using System.Text;
using Kuberkynesis.Agent.Core.Configuration;
using Kuberkynesis.Agent.Kube;

namespace Kuberkynesis.Agent.Startup;

public sealed class AgentStartupDiagnosticsPrinter
{
    public string BuildReport(AgentRuntimeOptions options, KubeBootstrapProbeResult probe, string? kubeConfigOverridePath)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(probe);

        var builder = new StringBuilder();
        builder.AppendLine("Startup diagnostics");
        builder.AppendLine($"Agent URL: {options.PublicUrl}");
        builder.AppendLine($"Browser auto-open: {(options.UiLaunch.AutoOpenBrowser ? "enabled" : "disabled")}");
        builder.AppendLine($"UI launch URL: {options.UiLaunch.Url}");
        builder.AppendLine($"Kubeconfig override: {FormatValue(kubeConfigOverridePath)}");
        builder.AppendLine($"kubectl: {(probe.KubectlAvailable ? "available" : "unavailable")}{FormatKubectlVersion(probe.KubectlClientVersion)}");
        builder.AppendLine($"Current context: {FormatValue(probe.CurrentContextName)}");
        builder.AppendLine($"Discovered contexts: {probe.ContextCount}");
        builder.AppendLine($"Interactive origins: {string.Join(", ", options.Origins.Interactive)}");
        builder.AppendLine($"Preview origin pattern: {options.Origins.PreviewPattern}");

        if (probe.SourcePaths.Count > 0)
        {
            builder.AppendLine("Resolved kubeconfig files:");

            foreach (var sourcePath in probe.SourcePaths)
            {
                builder.AppendLine($"- {sourcePath}");
            }
        }
        else
        {
            builder.AppendLine("Resolved kubeconfig files: none");
        }

        if (probe.Warnings.Count > 0)
        {
            builder.AppendLine("Warnings:");

            foreach (var warning in probe.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }
        else
        {
            builder.AppendLine("Warnings: none");
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "(none)"
            : value;
    }

    private static string FormatKubectlVersion(string? version)
    {
        return string.IsNullOrWhiteSpace(version)
            ? string.Empty
            : $" ({version})";
    }
}
