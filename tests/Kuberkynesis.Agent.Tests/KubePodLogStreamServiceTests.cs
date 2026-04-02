using Kuberkynesis.Agent.Kube;

namespace Kuberkynesis.Agent.Tests;

public sealed class KubePodLogStreamServiceTests
{
    [Fact]
    public async Task ReadBufferedAppendAsync_BatchesAdjacentLinesAndSkipsBlankLines()
    {
        using var reader = new StringReader("""
            2026-03-31T09:00:00Z first line

            2026-03-31T09:00:01Z second line
            2026-03-31T09:00:02Z third line
            """);

        var batch = await KubePodLogStreamService.ReadBufferedAppendAsync(reader, CancellationToken.None);

        Assert.NotNull(batch);
        Assert.Equal(3, batch.LineCount);
        Assert.Contains("first line", batch.Content, StringComparison.Ordinal);
        Assert.Contains("second line", batch.Content, StringComparison.Ordinal);
        Assert.Contains("third line", batch.Content, StringComparison.Ordinal);
        Assert.EndsWith(Environment.NewLine, batch.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadBufferedAppendAsync_StopsAtTheMaximumBatchSize()
    {
        var lines = Enumerable.Range(1, 30)
            .Select(index => $"2026-03-31T09:00:{index:00}Z line {index}");
        using var reader = new StringReader(string.Join(Environment.NewLine, lines) + Environment.NewLine);

        var batch = await KubePodLogStreamService.ReadBufferedAppendAsync(reader, CancellationToken.None);

        Assert.NotNull(batch);
        Assert.Equal(20, batch.LineCount);
        Assert.Contains("line 20", batch.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("line 21", batch.Content, StringComparison.Ordinal);
    }
}
