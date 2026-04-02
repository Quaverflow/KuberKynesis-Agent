using Microsoft.AspNetCore.Http;

namespace Kuberkynesis.Agent.Transport.Api;

public static class AgentSessionValidationPolicy
{
    public static bool RequiresHttpSessionValidation(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.Path.StartsWithSegments("/v1", StringComparison.OrdinalIgnoreCase) ||
            HttpMethods.IsOptions(request.Method) ||
            request.HttpContext.WebSockets.IsWebSocketRequest)
        {
            return false;
        }

        if (request.Path.Equals("/v1/hello", StringComparison.OrdinalIgnoreCase) ||
            request.Path.Equals("/v1/pair", StringComparison.OrdinalIgnoreCase) ||
            request.Path.Equals("/v1/session/release", StringComparison.OrdinalIgnoreCase) ||
            request.Path.Equals("/v1/live/stream", StringComparison.OrdinalIgnoreCase) ||
            request.Path.Equals("/v1/resources/watch", StringComparison.OrdinalIgnoreCase) ||
            request.Path.Equals("/v1/pods/logs/stream", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    public static string? TryGetBearerToken(string authorizationHeader)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader))
        {
            return null;
        }

        const string bearerPrefix = "Bearer ";

        return authorizationHeader.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase)
            ? authorizationHeader[bearerPrefix.Length..].Trim()
            : null;
    }
}
