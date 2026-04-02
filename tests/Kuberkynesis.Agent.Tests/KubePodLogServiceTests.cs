using k8s.Models;
using Kuberkynesis.Agent.Kube;

namespace Kuberkynesis.Agent.Tests;

public sealed class KubePodLogServiceTests
{
    [Fact]
    public void GetAvailableContainers_IncludesMainAndInitContainersWithoutDuplicates()
    {
        var pod = new V1Pod
        {
            Spec = new V1PodSpec
            {
                Containers =
                [
                    new V1Container { Name = "api" },
                    new V1Container { Name = "sidecar" }
                ],
                InitContainers =
                [
                    new V1Container { Name = "migrate" },
                    new V1Container { Name = "api" }
                ]
            }
        };

        var containers = KubePodLogService.GetAvailableContainers(pod);

        Assert.Equal(["api", "sidecar", "migrate"], containers);
    }

    [Fact]
    public void ResolveContainerName_UsesFirstContainerWhenNoPreferenceWasProvided()
    {
        var resolved = KubePodLogService.ResolveContainerName(null, ["api", "sidecar"]);

        Assert.Equal("api", resolved);
    }

    [Fact]
    public void ResolveContainerName_ThrowsForUnknownContainer()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            KubePodLogService.ResolveContainerName("worker", ["api", "sidecar"]));

        Assert.Contains("does not contain a container named 'worker'", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 200)]
    [InlineData(50, 50)]
    [InlineData(5000, 1000)]
    public void NormalizeTailLines_AppliesDefaultAndCap(int requested, int expected)
    {
        Assert.Equal(expected, KubePodLogService.NormalizeTailLines(requested));
    }
}
