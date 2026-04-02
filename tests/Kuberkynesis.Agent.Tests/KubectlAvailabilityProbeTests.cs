using Kuberkynesis.Agent.Kube;

namespace Kuberkynesis.Agent.Tests;

public sealed class KubectlAvailabilityProbeTests
{
    [Fact]
    public void TryExtractClientVersion_ReadsGitVersionFromJson()
    {
        const string json = """
            {
              "clientVersion": {
                "major": "1",
                "minor": "34",
                "gitVersion": "v1.34.2"
              }
            }
            """;

        var version = KubectlAvailabilityProbe.TryExtractClientVersion(json);

        Assert.Equal("v1.34.2", version);
    }

    [Fact]
    public void TryExtractClientVersion_ReturnsNullForInvalidJson()
    {
        var version = KubectlAvailabilityProbe.TryExtractClientVersion("not-json");

        Assert.Null(version);
    }
}
