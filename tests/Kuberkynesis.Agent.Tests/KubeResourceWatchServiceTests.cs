using System.Threading.Channels;
using Kuberkynesis.Agent.Kube;

namespace Kuberkynesis.Agent.Tests;

public sealed class KubeResourceWatchServiceTests
{
    [Fact]
    public void CreateSignalChannel_UsesABoundedSingleSignalBuffer()
    {
        var channel = KubeResourceWatchService.CreateSignalChannel();

        Assert.True(channel.Writer.TryWrite(true));
        Assert.True(channel.Writer.TryWrite(true));
        Assert.True(channel.Reader.TryRead(out _));
        Assert.False(channel.Reader.TryRead(out _));
    }

    [Fact]
    public void DrainPendingSignals_ClearsTheWakeBufferAndReturnsTheRawEventCount()
    {
        var channel = Channel.CreateUnbounded<bool>();
        channel.Writer.TryWrite(true);
        channel.Writer.TryWrite(true);

        var pendingEventCount = 5;

        var drainedEventCount = KubeResourceWatchService.DrainPendingSignals(channel.Reader, ref pendingEventCount);

        Assert.Equal(5, drainedEventCount);
        Assert.Equal(0, pendingEventCount);
        Assert.False(channel.Reader.TryRead(out _));
    }
}
