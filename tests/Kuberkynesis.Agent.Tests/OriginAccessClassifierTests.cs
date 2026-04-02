using Kuberkynesis.Agent.Core.Configuration;
using Kuberkynesis.Agent.Core.Security;
using Kuberkynesis.Ui.Shared.Connection;

namespace Kuberkynesis.Agent.Tests;

public sealed class OriginAccessClassifierTests
{
    private static readonly AgentRuntimeOptions Options = new()
    {
        Origins = new OriginOptions
        {
            Interactive =
            [
                "https://kuberkynesis.com",
                "https://kuberkynesis.pages.dev",
                "http://localhost:5173",
                "https://localhost:5173"
            ],
            PreviewPattern = "^https://[a-z0-9-]+\\.kuberkynesis\\.pages\\.dev$"
        }
    };

    [Fact]
    public void InteractiveOrigin_IsGrantedInteractiveAccess()
    {
        var classifier = new OriginAccessClassifier(Options);

        var decision = classifier.Evaluate("https://kuberkynesis.com");

        Assert.Equal(OriginAccessDecision.Allow(OriginAccessClass.Interactive), decision);
    }

    [Fact]
    public void PreviewOrigin_IsGrantedReadonlyPreviewAccess()
    {
        var classifier = new OriginAccessClassifier(Options);

        var decision = classifier.Evaluate("https://feature-123.kuberkynesis.pages.dev");

        Assert.Equal(OriginAccessDecision.Allow(OriginAccessClass.ReadonlyPreview), decision);
    }

    [Fact]
    public void UnknownOrigin_IsRejected()
    {
        var classifier = new OriginAccessClassifier(Options);

        var decision = classifier.Evaluate("https://example.com");

        Assert.Equal(OriginAccessDecision.Denied(), decision);
    }
}
