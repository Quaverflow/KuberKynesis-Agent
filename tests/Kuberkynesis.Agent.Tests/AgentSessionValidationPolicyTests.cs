using Kuberkynesis.Agent.Transport.Api;
using Microsoft.AspNetCore.Http;

namespace Kuberkynesis.Agent.Tests;

public sealed class AgentSessionValidationPolicyTests
{
    [Theory]
    [InlineData("GET", "/v1/session", true)]
    [InlineData("POST", "/v1/session/release", false)]
    [InlineData("POST", "/v1/session/ws-ticket", true)]
    [InlineData("GET", "/v1/diagnostics", true)]
    [InlineData("GET", "/v1/contexts", true)]
    [InlineData("POST", "/v1/workspace/resolve", true)]
    [InlineData("POST", "/v1/resources/query", true)]
    [InlineData("POST", "/v1/live/query", true)]
    [InlineData("GET", "/v1/hello", false)]
    [InlineData("POST", "/v1/pair", false)]
    [InlineData("GET", "/v1/live/stream", false)]
    [InlineData("GET", "/v1/resources/watch", false)]
    [InlineData("GET", "/v1/pods/logs/stream", false)]
    [InlineData("OPTIONS", "/v1/contexts", false)]
    public void RequiresHttpSessionValidation_MatchesTheExpectedEndpointPolicy(string method, string path, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;

        var result = AgentSessionValidationPolicy.RequiresHttpSessionValidation(context.Request);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void TryGetBearerToken_ExtractsTheTokenValue()
    {
        var token = AgentSessionValidationPolicy.TryGetBearerToken("Bearer pst_123");

        Assert.Equal("pst_123", token);
    }
}
