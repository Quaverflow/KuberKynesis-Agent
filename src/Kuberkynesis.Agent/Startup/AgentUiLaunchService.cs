using System.Diagnostics;
using Kuberkynesis.Agent.Core.Configuration;
using Kuberkynesis.Agent.Core.Security;

namespace Kuberkynesis.Agent.Startup;

public sealed class AgentUiLaunchService
{
    private readonly AgentRuntimeOptions runtimeOptions;
    private readonly PairingSessionRegistry sessions;
    private readonly OriginAccessClassifier classifier;
    private readonly ILogger<AgentUiLaunchService> logger;

    public AgentUiLaunchService(
        AgentRuntimeOptions runtimeOptions,
        PairingSessionRegistry sessions,
        OriginAccessClassifier classifier,
        ILogger<AgentUiLaunchService> logger)
    {
        this.runtimeOptions = runtimeOptions;
        this.sessions = sessions;
        this.classifier = classifier;
        this.logger = logger;
    }

    public void TryLaunchBrowser()
    {
        _ = Task.Run(TryLaunchBrowserAsync);
    }

    private async Task TryLaunchBrowserAsync()
    {
        if (!runtimeOptions.UiLaunch.AutoOpenBrowser || string.IsNullOrWhiteSpace(runtimeOptions.UiLaunch.Url))
        {
            return;
        }

        try
        {
            if (!Uri.TryCreate(runtimeOptions.UiLaunch.Url, UriKind.Absolute, out var uiUri))
            {
                logger.LogWarning("The configured UI URL '{UiUrl}' is not a valid absolute URI.", runtimeOptions.UiLaunch.Url);
                return;
            }

            if (IsLoopbackHttpUrl(uiUri))
            {
                var isReady = await EnsureLocalUiIsReadyAsync(uiUri);

                if (!isReady)
                {
                    return;
                }
            }

            var launchUrl = sessions.CreateUiLaunchUrl(
                runtimeOptions.UiLaunch.Url,
                runtimeOptions.PublicUrl,
                classifier,
                runtimeOptions.UiLaunch.AutoConnectWithPairingCode);

            Process.Start(new ProcessStartInfo
            {
                FileName = launchUrl,
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to launch the configured UI URL.");
        }
    }

    private async Task<bool> EnsureLocalUiIsReadyAsync(Uri uiUri)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(runtimeOptions.UiLaunch.ReadyTimeoutSeconds, 1, 120));
        var deadline = DateTimeOffset.UtcNow.Add(timeout);

        if (await WaitForHealthyAsync(uiUri, deadline))
        {
            return true;
        }

        logger.LogWarning(
            "The configured UI URL '{UiUrl}' did not become healthy within {TimeoutSeconds} seconds. The browser will not be opened.",
            uiUri,
            runtimeOptions.UiLaunch.ReadyTimeoutSeconds);

        return false;
    }

    private static async Task<bool> IsUiHealthyAsync(Uri uiUri)
    {
        try
        {
            using var httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(3)
            };

            using var response = await httpClient.GetAsync(uiUri.GetLeftPart(UriPartial.Path));
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> WaitForHealthyAsync(Uri uiUri, DateTimeOffset deadline)
    {
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await IsUiHealthyAsync(uiUri))
            {
                return true;
            }

            await Task.Delay(700);
        }

        return false;
    }

    private static bool IsLoopbackHttpUrl(Uri uri)
    {
        return uri.IsLoopback &&
               (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
    }
}
