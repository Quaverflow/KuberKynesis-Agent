using Kuberkynesis.Agent.Core.Configuration;
using Microsoft.Extensions.Configuration;

namespace Kuberkynesis.Agent.Tests;

public sealed class AgentRuntimeOptionsConfigurationTests
{
    [Fact]
    public void JsonConfiguration_UsesHostedUiLaunchDefaultsOutsideDevelopment()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(GetAgentProjectPath())
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        var options = configuration.GetSection(AgentRuntimeOptions.SectionName).Get<AgentRuntimeOptions>();

        Assert.NotNull(options);
        Assert.Equal(UiLaunchOptions.HostedProductionUrl, options!.UiLaunch.Url);
        Assert.True(options.UiLaunch.AutoOpenBrowser);
        Assert.Equal(4, options.ResourceQueries.ContextTimeoutSeconds);
    }

    [Fact]
    public void JsonConfiguration_UsesLocalUiLaunchDefaultsInDevelopment()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(GetAgentProjectPath())
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: false)
            .Build();

        var options = configuration.GetSection(AgentRuntimeOptions.SectionName).Get<AgentRuntimeOptions>();

        Assert.NotNull(options);
        Assert.Equal(UiLaunchOptions.LocalDevelopmentUrl, options!.UiLaunch.Url);
        Assert.True(options.UiLaunch.AutoOpenBrowser);
    }

    private static string GetAgentProjectPath()
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Kuberkynesis.Agent"));
    }
}
