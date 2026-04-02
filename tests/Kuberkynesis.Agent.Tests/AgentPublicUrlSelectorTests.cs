using System.Net;
using System.Net.Sockets;
using Kuberkynesis.Agent.Core.Configuration;

namespace Kuberkynesis.Agent.Tests;

public sealed class AgentPublicUrlSelectorTests
{
    [Theory]
    [InlineData("http://0.0.0.0:46321")]
    [InlineData("http://192.168.1.25:46321")]
    [InlineData("http://example.com:46321")]
    public void Resolve_Throws_WhenConfiguredUrlIsNotLoopback(string configuredUrl)
    {
        var exception = Assert.Throws<IOException>(() =>
            AgentPublicUrlSelector.Resolve(configuredUrl, requireExactPort: false));

        Assert.Contains("loopback host", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("46321")]
    [InlineData("http://127.0.0.1:0")]
    [InlineData("ftp://127.0.0.1:46321")]
    public void Resolve_Throws_WhenConfiguredUrlIsInvalid(string configuredUrl)
    {
        var exception = Assert.Throws<IOException>(() =>
            AgentPublicUrlSelector.Resolve(configuredUrl, requireExactPort: false));

        Assert.True(
            exception.Message.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("http or https", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("explicit port", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Resolve_FallsBackToAnotherLoopbackPort_WhenDefaultPortIsBusy()
    {
        using var listener = CreateBusyListener();
        var configuredUrl = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";

        var selection = AgentPublicUrlSelector.Resolve(configuredUrl, requireExactPort: false);

        Assert.NotEqual(configuredUrl, selection.SelectedUrl);
        Assert.NotNull(selection.Notice);
        Assert.Contains("Using", selection.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_Throws_WhenExactPortIsRequiredAndBusy()
    {
        using var listener = CreateBusyListener();
        var configuredUrl = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";

        var exception = Assert.Throws<IOException>(() =>
            AgentPublicUrlSelector.Resolve(configuredUrl, requireExactPort: true));

        Assert.Contains("already in use", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static TcpListener CreateBusyListener()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return listener;
    }
}
