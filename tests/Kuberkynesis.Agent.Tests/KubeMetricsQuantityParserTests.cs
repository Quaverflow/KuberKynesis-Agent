using Kuberkynesis.Agent.Kube;

namespace Kuberkynesis.Agent.Tests;

public sealed class KubeMetricsQuantityParserTests
{
    [Theory]
    [InlineData("250m", 250)]
    [InlineData("1", 1000)]
    [InlineData("250u", 0)]
    [InlineData("1000000n", 1)]
    public void ParseCpuMillicores_ParsesCommonCpuQuantities(string value, long expectedMillicores)
    {
        var parsed = KubeMetricsQuantityParser.ParseCpuMillicores(value);

        Assert.Equal(expectedMillicores, parsed);
    }

    [Theory]
    [InlineData("64Mi", 67108864L)]
    [InlineData("1Gi", 1073741824L)]
    [InlineData("128974848", 128974848L)]
    [InlineData("512Ki", 524288L)]
    public void ParseBytes_ParsesCommonMemoryQuantities(string value, long expectedBytes)
    {
        var parsed = KubeMetricsQuantityParser.ParseBytes(value);

        Assert.Equal(expectedBytes, parsed);
    }
}
