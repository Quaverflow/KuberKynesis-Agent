using Microsoft.AspNetCore.Http;

namespace Kuberkynesis.Agent.Transport.Api;

public static class AgentBridgeOriginResolver
{
    public const string ForwardedOriginHeaderName = "X-Kuberkynesis-Bridge-Origin";
    public const string ForwardedOriginQueryParameterName = "bridgeOrigin";

    public static string? ResolveEffectiveOrigin(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var headerOrigin = NormalizeOriginValue(request.Headers.Origin.ToString());
        var forwardedOrigin = request.Headers[ForwardedOriginHeaderName].ToString();

        if ((IsTrustedBridgeOrigin(headerOrigin) || string.IsNullOrWhiteSpace(headerOrigin)) &&
            !string.IsNullOrWhiteSpace(forwardedOrigin))
        {
            return forwardedOrigin.Trim();
        }

        if (!string.IsNullOrWhiteSpace(headerOrigin))
        {
            return headerOrigin;
        }

        var queryOrigin = request.Query[ForwardedOriginQueryParameterName].ToString();
        return string.IsNullOrWhiteSpace(queryOrigin)
            ? null
            : queryOrigin.Trim();
    }

    private static string? NormalizeOriginValue(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin))
        {
            return null;
        }

        return string.Equals(origin.Trim(), "null", StringComparison.OrdinalIgnoreCase)
            ? null
            : origin.Trim();
    }

    private static bool IsTrustedBridgeOrigin(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin))
        {
            return false;
        }

        return origin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase) ||
               origin.StartsWith("edge-extension://", StringComparison.OrdinalIgnoreCase) ||
               origin.StartsWith("moz-extension://", StringComparison.OrdinalIgnoreCase);
    }
}
