using System.Diagnostics;
using System.Text.Json;

namespace Kuberkynesis.Agent.Kube;

public sealed record KubectlAvailabilityProbeResult(
    bool IsAvailable,
    string? ClientVersion,
    string? Warning);

public interface IKubectlAvailabilityProbe
{
    KubectlAvailabilityProbeResult Probe();
}

public sealed class KubectlAvailabilityProbe : IKubectlAvailabilityProbe
{
    public KubectlAvailabilityProbeResult Probe()
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "kubectl",
                    Arguments = "version --client --output=json",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();

            if (!process.WaitForExit(5000))
            {
                TryTerminate(process);
                return new KubectlAvailabilityProbeResult(
                    IsAvailable: false,
                    ClientVersion: null,
                    Warning: "kubectl did not respond within 5 seconds.");
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();

            if (process.ExitCode != 0)
            {
                return new KubectlAvailabilityProbeResult(
                    IsAvailable: false,
                    ClientVersion: null,
                    Warning: NormalizeWarning(stderr) ?? NormalizeWarning(stdout) ?? $"kubectl exited with code {process.ExitCode}.");
            }

            return new KubectlAvailabilityProbeResult(
                IsAvailable: true,
                ClientVersion: TryExtractClientVersion(stdout),
                Warning: null);
        }
        catch (Exception exception)
        {
            return new KubectlAvailabilityProbeResult(
                IsAvailable: false,
                ClientVersion: null,
                Warning: $"kubectl is not available: {exception.Message}");
        }
    }

    public static string? TryExtractClientVersion(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.TryGetProperty("clientVersion", out var clientVersion) &&
                clientVersion.TryGetProperty("gitVersion", out var gitVersion))
            {
                return gitVersion.GetString();
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static string? NormalizeWarning(string? warning)
    {
        return string.IsNullOrWhiteSpace(warning)
            ? null
            : warning.Trim();
    }

    private static void TryTerminate(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Ignore best-effort cleanup failures.
        }
    }
}
