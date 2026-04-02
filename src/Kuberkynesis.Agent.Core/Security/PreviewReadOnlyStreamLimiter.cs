using Kuberkynesis.Agent.Core.Configuration;
using Kuberkynesis.Ui.Shared.Connection;

namespace Kuberkynesis.Agent.Core.Security;

public enum PreviewReadOnlyStreamKind
{
    ResourceWatch,
    PodLog
}

public sealed class PreviewReadOnlyStreamLimiter
{
    private static readonly IDisposable NoopLease = new NoopDisposable();

    private readonly object gate = new();
    private readonly int maxConcurrentStreams;
    private readonly int maxWatchCountPerSession;
    private readonly int maxLogStreamsPerSession;
    private readonly Dictionary<string, SessionCounters> activeCounts = new(StringComparer.Ordinal);

    public PreviewReadOnlyStreamLimiter(AgentRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        maxConcurrentStreams = Math.Max(1, options.PreviewReadOnlyLimits.MaxConcurrentStreams);
        maxWatchCountPerSession = Math.Max(1, options.PreviewReadOnlyLimits.MaxWatchCountPerSession);
        maxLogStreamsPerSession = Math.Max(1, options.PreviewReadOnlyLimits.MaxLogStreamsPerSession);
    }

    public StreamLeaseResult TryAcquire(AuthenticatedAgentSession session, PreviewReadOnlyStreamKind streamKind)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (session.GrantedMode is not OriginAccessClass.ReadonlyPreview)
        {
            return StreamLeaseResult.Allowed(NoopLease);
        }

        lock (gate)
        {
            if (!activeCounts.TryGetValue(session.SessionToken, out var counters))
            {
                counters = new SessionCounters();
                activeCounts[session.SessionToken] = counters;
            }

            if (streamKind is PreviewReadOnlyStreamKind.ResourceWatch &&
                counters.ResourceWatchCount >= maxWatchCountPerSession)
            {
                return StreamLeaseResult.Denied(
                    $"Readonly preview sessions are limited to {maxWatchCountPerSession} concurrent resource watch streams.");
            }

            if (streamKind is PreviewReadOnlyStreamKind.PodLog &&
                counters.PodLogCount >= maxLogStreamsPerSession)
            {
                return StreamLeaseResult.Denied(
                    $"Readonly preview sessions are limited to {maxLogStreamsPerSession} concurrent pod log streams.");
            }

            if (counters.TotalCount >= maxConcurrentStreams)
            {
                return StreamLeaseResult.Denied(
                    $"Readonly preview sessions are limited to {maxConcurrentStreams} concurrent live streams.");
            }

            counters.TotalCount++;

            if (streamKind is PreviewReadOnlyStreamKind.ResourceWatch)
            {
                counters.ResourceWatchCount++;
            }
            else
            {
                counters.PodLogCount++;
            }

            return StreamLeaseResult.Allowed(new Lease(this, session.SessionToken, streamKind));
        }
    }

    private void Release(string sessionToken, PreviewReadOnlyStreamKind streamKind)
    {
        lock (gate)
        {
            if (!activeCounts.TryGetValue(sessionToken, out var counters))
            {
                return;
            }

            counters.TotalCount = Math.Max(0, counters.TotalCount - 1);

            if (streamKind is PreviewReadOnlyStreamKind.ResourceWatch)
            {
                counters.ResourceWatchCount = Math.Max(0, counters.ResourceWatchCount - 1);
            }
            else
            {
                counters.PodLogCount = Math.Max(0, counters.PodLogCount - 1);
            }

            if (counters.TotalCount is 0 &&
                counters.ResourceWatchCount is 0 &&
                counters.PodLogCount is 0)
            {
                activeCounts.Remove(sessionToken);
            }
        }
    }

    public sealed record StreamLeaseResult(bool Success, string? ErrorMessage, IDisposable Lease)
    {
        public static StreamLeaseResult Allowed(IDisposable lease) => new(true, null, lease);

        public static StreamLeaseResult Denied(string errorMessage) => new(false, errorMessage, NoopLease);
    }

    private sealed class Lease : IDisposable
    {
        private readonly PreviewReadOnlyStreamLimiter owner;
        private readonly string sessionToken;
        private readonly PreviewReadOnlyStreamKind streamKind;
        private bool disposed;

        public Lease(PreviewReadOnlyStreamLimiter owner, string sessionToken, PreviewReadOnlyStreamKind streamKind)
        {
            this.owner = owner;
            this.sessionToken = sessionToken;
            this.streamKind = streamKind;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            owner.Release(sessionToken, streamKind);
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class SessionCounters
    {
        public int TotalCount { get; set; }

        public int ResourceWatchCount { get; set; }

        public int PodLogCount { get; set; }
    }
}
