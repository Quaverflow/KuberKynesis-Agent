using Kuberkynesis.Agent.Core.Configuration;

namespace Kuberkynesis.Agent.Tests;

public sealed class AgentStartupCliOverridesTests
{
    [Fact]
    public void ParseAndApply_OverridesPortOriginsAndBrowserLaunch()
    {
        var overrides = AgentStartupCliOverrides.Parse(
        [
            "--port", "47321",
            "--origin", "http://localhost:4173",
            "--origin=http://localhost:4173",
            "--no-browser-open"
        ]);

        var options = new AgentRuntimeOptions
        {
            PublicUrl = "http://127.0.0.1:46321",
            Origins = new OriginOptions
            {
                Interactive = ["http://localhost:5173"]
            },
            UiLaunch = new UiLaunchOptions
            {
                AutoOpenBrowser = true
            }
        };

        overrides.ApplyTo(options);

        Assert.Equal("http://127.0.0.1:47321", options.PublicUrl);
        Assert.Equal(
        [
            "http://localhost:5173",
            "http://localhost:4173"
        ],
        options.Origins.Interactive);
        Assert.False(options.UiLaunch.AutoOpenBrowser);
    }

    [Fact]
    public void Parse_CapturesKubeconfigAndDiagnosticsFlags()
    {
        var overrides = AgentStartupCliOverrides.Parse(
        [
            "--kubeconfig", @"C:\temp\lab.kubeconfig",
            "--diagnostics"
        ]);

        Assert.Equal(@"C:\temp\lab.kubeconfig", overrides.KubeConfigPath);
        Assert.True(overrides.EnableDiagnostics);
    }

    [Fact]
    public void Parse_AllowsStartSubcommandBeforeFlags()
    {
        var overrides = AgentStartupCliOverrides.Parse(
        [
            "start",
            "--diagnostics",
            "--no-browser-open"
        ]);

        Assert.True(overrides.EnableDiagnostics);
        Assert.True(overrides.DisableBrowserOpen);
    }

    [Fact]
    public void Parse_ThrowsForUnknownPositionalArgument()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            AgentStartupCliOverrides.Parse(["launch"]));

        Assert.Contains("not supported", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ThrowsForInvalidPorts()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            AgentStartupCliOverrides.Parse(["--port", "70000"]));

        Assert.Contains("--port", exception.Message, StringComparison.Ordinal);
    }
}
