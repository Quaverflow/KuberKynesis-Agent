using Kuberkynesis.Agent.Core.Configuration;
using Kuberkynesis.Agent.Core.Security;
using Kuberkynesis.Ui.Shared.Connection;

namespace Kuberkynesis.Agent.Tests;

public sealed class PreviewReadOnlyStreamLimiterTests
{
    [Fact]
    public void TryAcquire_DoesNotLimitInteractiveSessions()
    {
        var limiter = CreateLimiter(maxConcurrentStreams: 1, maxWatchCount: 1, maxLogStreams: 1);
        var session = CreateSession("pst_interactive", OriginAccessClass.Interactive);

        using var first = limiter.TryAcquire(session, PreviewReadOnlyStreamKind.ResourceWatch).Lease;
        var second = limiter.TryAcquire(session, PreviewReadOnlyStreamKind.ResourceWatch);

        Assert.True(second.Success);
    }

    [Fact]
    public void TryAcquire_EnforcesPreviewLogStreamLimit()
    {
        var limiter = CreateLimiter(maxConcurrentStreams: 8, maxWatchCount: 8, maxLogStreams: 1);
        var session = CreateSession("pst_preview_logs", OriginAccessClass.ReadonlyPreview);

        using var first = limiter.TryAcquire(session, PreviewReadOnlyStreamKind.PodLog).Lease;
        var second = limiter.TryAcquire(session, PreviewReadOnlyStreamKind.PodLog);

        Assert.False(second.Success);
        Assert.Contains("pod log streams", second.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryAcquire_EnforcesPreviewWatchStreamLimit()
    {
        var limiter = CreateLimiter(maxConcurrentStreams: 8, maxWatchCount: 1, maxLogStreams: 8);
        var session = CreateSession("pst_preview_watch", OriginAccessClass.ReadonlyPreview);

        using var first = limiter.TryAcquire(session, PreviewReadOnlyStreamKind.ResourceWatch).Lease;
        var second = limiter.TryAcquire(session, PreviewReadOnlyStreamKind.ResourceWatch);

        Assert.False(second.Success);
        Assert.Contains("resource watch streams", second.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryAcquire_EnforcesPreviewConcurrentStreamLimitAcrossKinds()
    {
        var limiter = CreateLimiter(maxConcurrentStreams: 2, maxWatchCount: 8, maxLogStreams: 8);
        var session = CreateSession("pst_preview_total", OriginAccessClass.ReadonlyPreview);

        using var first = limiter.TryAcquire(session, PreviewReadOnlyStreamKind.ResourceWatch).Lease;
        using var second = limiter.TryAcquire(session, PreviewReadOnlyStreamKind.PodLog).Lease;
        var third = limiter.TryAcquire(session, PreviewReadOnlyStreamKind.ResourceWatch);

        Assert.False(third.Success);
        Assert.Contains("concurrent live streams", third.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryAcquire_ReleasesCapacityWhenTheLeaseIsDisposed()
    {
        var limiter = CreateLimiter(maxConcurrentStreams: 1, maxWatchCount: 1, maxLogStreams: 1);
        var session = CreateSession("pst_preview_release", OriginAccessClass.ReadonlyPreview);

        var first = limiter.TryAcquire(session, PreviewReadOnlyStreamKind.ResourceWatch);
        Assert.True(first.Success);
        first.Lease.Dispose();

        var second = limiter.TryAcquire(session, PreviewReadOnlyStreamKind.ResourceWatch);

        Assert.True(second.Success);
    }

    [Fact]
    public void TryAcquire_TracksLimitsPerSessionToken()
    {
        var limiter = CreateLimiter(maxConcurrentStreams: 1, maxWatchCount: 1, maxLogStreams: 1);
        var firstSession = CreateSession("pst_preview_a", OriginAccessClass.ReadonlyPreview);
        var secondSession = CreateSession("pst_preview_b", OriginAccessClass.ReadonlyPreview);

        using var first = limiter.TryAcquire(firstSession, PreviewReadOnlyStreamKind.ResourceWatch).Lease;
        var second = limiter.TryAcquire(secondSession, PreviewReadOnlyStreamKind.ResourceWatch);

        Assert.True(second.Success);
    }

    private static PreviewReadOnlyStreamLimiter CreateLimiter(int maxConcurrentStreams, int maxWatchCount, int maxLogStreams)
    {
        return new PreviewReadOnlyStreamLimiter(new AgentRuntimeOptions
        {
            PreviewReadOnlyLimits = new PreviewReadOnlyLimitsOptions
            {
                MaxConcurrentStreams = maxConcurrentStreams,
                MaxWatchCountPerSession = maxWatchCount,
                MaxLogStreamsPerSession = maxLogStreams
            }
        });
    }

    private static AuthenticatedAgentSession CreateSession(string token, OriginAccessClass accessClass)
    {
        return new AuthenticatedAgentSession(
            token,
            "csrf_test",
            accessClass,
            "https://preview.kuberkynesis-ui.pages.dev",
            "1.0.0",
            DateTimeOffset.UtcNow.AddHours(1));
    }
}
