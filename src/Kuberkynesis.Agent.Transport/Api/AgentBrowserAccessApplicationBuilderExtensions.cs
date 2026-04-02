using Kuberkynesis.Agent.Core.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Kuberkynesis.Agent.Transport.Api;

public static class AgentBrowserAccessApplicationBuilderExtensions
{
    public static IApplicationBuilder UseAgentBrowserAccess(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/v1", StringComparison.OrdinalIgnoreCase))
            {
                await next();
                return;
            }

            var headerOrigin = context.Request.Headers.Origin.ToString();
            var origin = AgentBridgeOriginResolver.ResolveEffectiveOrigin(context.Request);

            if (string.IsNullOrWhiteSpace(origin))
            {
                if (HttpMethods.IsOptions(context.Request.Method))
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        error = "CORS preflight requests must include an Origin header."
                    });

                    return;
                }

                await next();
                return;
            }

            var classifier = context.RequestServices.GetRequiredService<OriginAccessClassifier>();
            var decision = classifier.Evaluate(origin);

            if (!decision.IsAllowed)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "The request origin is not allowed."
                });

                return;
            }

            if (!string.IsNullOrWhiteSpace(headerOrigin))
            {
                ApplyCorsHeaders(context.Response.Headers, origin);
            }

            if (HttpMethods.IsOptions(context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }

            await next();
        });
    }

    private static void ApplyCorsHeaders(IHeaderDictionary headers, string origin)
    {
        headers.AccessControlAllowOrigin = origin;
        headers.AccessControlAllowMethods = "GET,POST,DELETE,OPTIONS";
        headers.AccessControlAllowHeaders = "Authorization,Content-Type,X-Kuberkynesis-Csrf";
        headers.AccessControlMaxAge = "600";
        headers.Append("Vary", "Origin");
    }
}
