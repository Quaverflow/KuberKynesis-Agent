using System.Text.RegularExpressions;
using Kuberkynesis.Agent.Core.Configuration;
using Kuberkynesis.Agent.Core.Security;
using Kuberkynesis.Agent.Transport.Api;
using Kuberkynesis.Ui.Shared.Connection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Kuberkynesis.Agent.Tests;

public sealed class AgentHttpMiddlewareTests
{
    [Fact]
    public async Task BrowserAccessMiddleware_AllowsPreflightForConfiguredOrigins()
    {
        var services = BuildServices();
        var pipeline = BuildPipeline(services);
        var context = CreateContext(
            services,
            HttpMethods.Options,
            "/v1/contexts",
            origin: "http://localhost:5173");

        await pipeline(context);

        Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
        Assert.Equal("http://localhost:5173", context.Response.Headers.AccessControlAllowOrigin);
        Assert.Contains("DELETE", context.Response.Headers.AccessControlAllowMethods.ToString(), StringComparison.Ordinal);
        Assert.Contains("Authorization", context.Response.Headers.AccessControlAllowHeaders.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionValidationMiddleware_RejectsMissingBearerTokens()
    {
        var services = BuildServices();
        var pipeline = BuildPipeline(services);
        var context = CreateContext(
            services,
            HttpMethods.Get,
            "/v1/contexts",
            origin: "http://localhost:5173");

        await pipeline(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task SessionValidationMiddleware_AllowsAuthenticatedRequests()
    {
        var services = BuildServices();
        var pipeline = BuildPipeline(services);
        var sessionToken = CreateInteractiveSessionToken(services, "http://localhost:5173");
        var context = CreateContext(
            services,
            HttpMethods.Get,
            "/v1/contexts",
            origin: "http://localhost:5173",
            bearerToken: sessionToken);

        await pipeline(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(context.TryGetAuthenticatedAgentSession(out var session));
        Assert.Equal(sessionToken, session?.SessionToken);
    }

    [Fact]
    public async Task SessionValidationMiddleware_AllowsBrowserBridgeRequestsUsingTheForwardedOrigin()
    {
        var services = BuildServices();
        var pipeline = BuildPipeline(services);
        const string pageOrigin = "https://kuberkynesis.pages.dev";
        var sessionToken = CreateInteractiveSessionToken(services, pageOrigin);
        var context = CreateContext(
            services,
            HttpMethods.Get,
            "/v1/contexts",
            origin: "chrome-extension://bridge-test",
            bearerToken: sessionToken);
        context.Request.Headers[AgentBridgeOriginResolver.ForwardedOriginHeaderName] = pageOrigin;

        await pipeline(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(context.TryGetAuthenticatedAgentSession(out var session));
        Assert.Equal(sessionToken, session?.SessionToken);
    }

    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var options = new AgentRuntimeOptions
        {
            Origins = new OriginOptions
            {
                Interactive = ["http://localhost:5173", "https://kuberkynesis.pages.dev"],
                PreviewPattern = "^https://preview.example$"
            }
        };

        services.AddSingleton(options);
        services.AddSingleton<OriginAccessClassifier>();
        services.AddSingleton<PairingSessionRegistry>();

        return services.BuildServiceProvider();
    }

    private static RequestDelegate BuildPipeline(IServiceProvider services)
    {
        var app = new ApplicationBuilder(services);
        app.UseAgentBrowserAccess();
        app.UseAgentSessionValidation();
        app.Run(context =>
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        });
        return app.Build();
    }

    private static DefaultHttpContext CreateContext(
        IServiceProvider services,
        string method,
        string path,
        string? origin = null,
        string? bearerToken = null)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = services
        };

        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();

        if (!string.IsNullOrWhiteSpace(origin))
        {
            context.Request.Headers.Origin = origin;
        }

        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            context.Request.Headers.Authorization = $"Bearer {bearerToken}";
        }

        return context;
    }

    private static string CreateInteractiveSessionToken(IServiceProvider services, string origin)
    {
        var options = services.GetRequiredService<AgentRuntimeOptions>();
        var classifier = services.GetRequiredService<OriginAccessClassifier>();
        var registry = services.GetRequiredService<PairingSessionRegistry>();
        var banner = registry.CreateStartupBanner(options);
        var pairingCode = Regex.Match(banner, "Pairing code: (?<code>[A-Z0-9]+)").Groups["code"].Value;
        var hello = registry.CreateHelloResponse(classifier);
        var result = registry.TryPair(
            new PairRequest
            {
                Nonce = hello.Nonce,
                AppVersion = "1.0.0",
                PairingCode = pairingCode,
                Origin = origin,
                RequestedMode = OriginAccessClass.Interactive
            },
            origin,
            classifier.Evaluate(origin));

        Assert.True(result.Success);
        return result.Response!.SessionToken;
    }
}
