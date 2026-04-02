using System.Net;
using System.Net.Sockets;

namespace Kuberkynesis.Agent.Core.Configuration;

public sealed record AgentPublicUrlSelection(
    string SelectedUrl,
    string? Notice);

public static class AgentPublicUrlSelector
{
    public static AgentPublicUrlSelection Resolve(string configuredUrl, bool requireExactPort)
    {
        if (!Uri.TryCreate(configuredUrl, UriKind.Absolute, out var configuredUri))
        {
            throw new IOException(
                $"The agent public URL '{configuredUrl}' is invalid. Use an absolute loopback URL such as http://127.0.0.1:46321 or http://localhost:46321.");
        }

        if (!string.Equals(configuredUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(configuredUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException(
                $"The agent public URL '{configuredUrl}' must use http or https.");
        }

        if (!configuredUri.IsLoopback)
        {
            throw new IOException(
                $"The agent public URL '{configuredUrl}' must use a loopback host. Wildcard, LAN, and remote hosts are not allowed.");
        }

        if (configuredUri.Port <= 0)
        {
            throw new IOException(
                $"The agent public URL '{configuredUrl}' must include an explicit port.");
        }

        if (IsPortAvailable(configuredUri))
        {
            return new AgentPublicUrlSelection(configuredUrl, null);
        }

        if (requireExactPort)
        {
            throw new IOException($"The requested agent port {configuredUri.Port} is already in use on {configuredUri.Host}.");
        }

        var fallbackUri = FindAvailableLoopbackUri(configuredUri);

        if (fallbackUri is null)
        {
            throw new IOException($"No available loopback port was found after {configuredUri.Port} for {configuredUri.Scheme}://{configuredUri.Host}.");
        }

        return new AgentPublicUrlSelection(
            fallbackUri.GetLeftPart(UriPartial.Authority),
            $"Default agent port {configuredUri.Port} was unavailable. Using {fallbackUri.GetLeftPart(UriPartial.Authority)} instead.");
    }

    private static Uri? FindAvailableLoopbackUri(Uri configuredUri)
    {
        for (var offset = 1; offset <= 25; offset++)
        {
            var candidate = new UriBuilder(configuredUri)
            {
                Port = configuredUri.Port + offset
            }.Uri;

            if (IsPortAvailable(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool IsPortAvailable(Uri uri)
    {
        var address = uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            ? IPAddress.Loopback
            : IPAddress.TryParse(uri.Host, out var parsedAddress)
                ? parsedAddress
                : IPAddress.Loopback;

        try
        {
            using var listener = new TcpListener(address, uri.Port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
