using Kuberkynesis.Agent.Core.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Kuberkynesis.Agent.Transport.Api;

public static class AgentSessionValidationApplicationBuilderExtensions
{
    public static IApplicationBuilder UseAgentSessionValidation(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            if (!AgentSessionValidationPolicy.RequiresHttpSessionValidation(context.Request))
            {
                await next();
                return;
            }

            var sessions = context.RequestServices.GetRequiredService<PairingSessionRegistry>();
            var sessionToken = AgentSessionValidationPolicy.TryGetBearerToken(context.Request.Headers.Authorization.ToString());
            var origin = context.Request.Headers.Origin.ToString();
            var authorization = sessions.AuthorizeSession(sessionToken, origin);

            if (!authorization.Success || authorization.Session is null)
            {
                context.Response.StatusCode = authorization.StatusCode;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = authorization.ErrorMessage
                });

                return;
            }

            context.SetAuthenticatedAgentSession(authorization.Session);
            await next();
        });
    }
}
