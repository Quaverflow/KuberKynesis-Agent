using Kuberkynesis.Agent.Core.Security;
using Microsoft.AspNetCore.Http;

namespace Kuberkynesis.Agent.Transport.Api;

public static class AgentSessionHttpContextExtensions
{
    private const string AuthenticatedAgentSessionItemKey = "__Kuberkynesis.AuthenticatedAgentSession";

    public static void SetAuthenticatedAgentSession(this HttpContext httpContext, AuthenticatedAgentSession session)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(session);

        httpContext.Items[AuthenticatedAgentSessionItemKey] = session;
    }

    public static bool TryGetAuthenticatedAgentSession(this HttpContext httpContext, out AuthenticatedAgentSession? session)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (httpContext.Items.TryGetValue(AuthenticatedAgentSessionItemKey, out var value) &&
            value is AuthenticatedAgentSession authenticatedSession)
        {
            session = authenticatedSession;
            return true;
        }

        session = null;
        return false;
    }
}
