using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using Kuberkynesis.Agent.Core.Configuration;
using Kuberkynesis.Ui.Shared.Connection;

namespace Kuberkynesis.Agent.Core.Security;

public sealed class PairingSessionRegistry : IDisposable
{
    private const string PairingAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private readonly object gate = new();
    private readonly AgentRuntimeOptions options;
    private readonly TimeProvider timeProvider;
    private readonly ITimer? pairingCodeRotationTimer;
    private readonly string agentInstanceId;
    private readonly string agentVersion;
    private readonly Dictionary<string, DateTimeOffset> nonces = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingWebSocketTicket> webSocketTickets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingSessionRelease> pendingSessionReleases = new(StringComparer.Ordinal);
    private readonly List<ActiveSession> readonlySessions = [];
    private ActiveSession? interactiveSession;
    private string pairingCode;
    private DateTimeOffset pairingCodeExpiresAtUtc;

    public PairingSessionRegistry(AgentRuntimeOptions options, TimeProvider? timeProvider = null)
    {
        this.options = options;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        agentInstanceId = CreateToken("agt_", 12);
        agentVersion = string.IsNullOrWhiteSpace(options.AdvertisedVersionOverride)
            ? Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0"
            : options.AdvertisedVersionOverride.Trim();
        pairingCode = CreatePairingCode(options.Pairing.CodeLength);
        pairingCodeExpiresAtUtc = GetUtcNow().AddMinutes(options.Pairing.PairingCodeRotationMinutes);
        pairingCodeRotationTimer = options.Pairing.PairingCodeRotationMinutes > 0
            ? this.timeProvider.CreateTimer(HandlePairingCodeRotationTimerTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan)
            : null;
        SchedulePairingCodeRotationTimer();
    }

    public string AgentInstanceId => agentInstanceId;

    public string AgentVersion => agentVersion;

    public event Action<PairingCodeRotationNotice>? PairingCodeRotated;

    public HelloResponse CreateHelloResponse(OriginAccessClassifier classifier)
    {
        RotatePairingCodeIfNeededAndNotify();

        lock (gate)
        {
            var nonce = CreateToken("n_", 12);
            nonces[nonce] = GetUtcNow().AddSeconds(options.Pairing.NonceLifetimeSeconds);

            return new HelloResponse(
                AgentInstanceId: agentInstanceId,
                AgentVersion: agentVersion,
                PairingRequired: true,
                PairingMode: PairingMode.CodeEntry,
                Nonce: nonce,
                Capabilities: new AgentCapabilities(
                    Mutations: true,
                    Logs: true,
                    Exec: true,
                    PortForward: false,
                    LiveStreams: true),
                AllowedOrigins: classifier.InteractiveOrigins,
                PreviewOriginPattern: classifier.PreviewPattern);
        }
    }

    public PairingAttemptResult TryPair(PairRequest request, string? origin, OriginAccessDecision decision)
    {
        RotatePairingCodeIfNeededAndNotify();

        lock (gate)
        {
            RemoveExpiredState();

            if (!decision.IsAllowed || decision.AccessClass is null)
            {
                return new PairingAttemptResult(false, null, "The request origin is not allowed to pair with the agent.", 403);
            }

            if (string.IsNullOrWhiteSpace(origin))
            {
                return new PairingAttemptResult(false, null, "A valid request origin is required.", 400);
            }

            if (!nonces.Remove(request.Nonce, out var nonceExpiry) || nonceExpiry < GetUtcNow())
            {
                return new PairingAttemptResult(false, null, "The pairing nonce is missing or expired.", 400);
            }

            if (!string.Equals(pairingCode, request.PairingCode, StringComparison.OrdinalIgnoreCase))
            {
                return new PairingAttemptResult(false, null, "The pairing code is invalid.", 403);
            }

            if (decision.AccessClass is OriginAccessClass.Interactive && interactiveSession is not null)
            {
                if (pendingSessionReleases.ContainsKey(interactiveSession.SessionToken) || request.TakeoverInteractiveSession)
                {
                    RevokeSessionByToken(interactiveSession.SessionToken);
                }
                else
                {
                    return new PairingAttemptResult(
                        false,
                        null,
                        $"Interactive control is already held by {interactiveSession.Origin}. Confirm takeover if you want to replace that session.",
                        409);
                }
            }

            var grantedMode = decision.AccessClass.Value;
            var session = ActiveSession.Create(
                grantedMode,
                origin,
                request.AppVersion,
                GetUtcNow().AddHours(options.Pairing.SessionLifetimeHours));

            if (grantedMode is OriginAccessClass.Interactive)
            {
                interactiveSession = session;
            }
            else
            {
                readonlySessions.Add(session);
            }

            return new PairingAttemptResult(
                true,
                new PairResponse(
                    session.SessionToken,
                    session.ExpiresAtUtc,
                    session.CsrfToken,
                    agentInstanceId,
                    session.GrantedMode),
                null,
                200);
        }
    }

    public SessionAuthorizationResult AuthorizeSession(string? sessionToken, string? origin)
    {
        lock (gate)
        {
            RemoveExpiredState();

            if (string.IsNullOrWhiteSpace(sessionToken))
            {
                return new SessionAuthorizationResult(false, null, "A bearer session token is required.", 401);
            }

            var session = FindSession(sessionToken);

            if (session is null)
            {
                return new SessionAuthorizationResult(false, null, "The session token is invalid or expired.", 401);
            }

            if (!string.IsNullOrWhiteSpace(origin) &&
                !string.Equals(origin, session.Origin, StringComparison.OrdinalIgnoreCase))
            {
                return new SessionAuthorizationResult(false, null, "The request origin does not match the active session.", 403);
            }

            pendingSessionReleases.Remove(session.SessionToken);

            return new SessionAuthorizationResult(
                true,
                new AuthenticatedAgentSession(
                    session.SessionToken,
                    session.CsrfToken,
                    session.GrantedMode,
                    session.Origin,
                    session.AppVersion,
                    session.ExpiresAtUtc),
                null,
                200);
        }
    }

    public bool ScheduleSessionRelease(string? sessionToken, string? origin)
    {
        lock (gate)
        {
            RemoveExpiredState();

            if (string.IsNullOrWhiteSpace(sessionToken))
            {
                return false;
            }

            var session = FindSession(sessionToken);

            if (session is null)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(origin) &&
                !string.Equals(origin, session.Origin, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            pendingSessionReleases[session.SessionToken] = new PendingSessionRelease(
                SessionToken: session.SessionToken,
                Origin: session.Origin,
                ReleaseAtUtc: GetUtcNow().AddSeconds(options.Pairing.DisconnectReleaseGraceSeconds));

            return true;
        }
    }

    public bool RevokeSession(AuthenticatedAgentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        lock (gate)
        {
            RemoveExpiredState();
            return RevokeSessionByToken(session.SessionToken);
        }
    }

    public WebSocketTicketResponse CreateWebSocketTicket(AuthenticatedAgentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        lock (gate)
        {
            RemoveExpiredState();
            var now = GetUtcNow();

            var activeSession = FindSession(session.SessionToken);

            if (activeSession is null)
            {
                throw new InvalidOperationException("The authenticated agent session is no longer active.");
            }

            var expiresAtUtc = DateTimeOffset.Compare(
                activeSession.ExpiresAtUtc,
                now.AddSeconds(options.Pairing.WebSocketTicketLifetimeSeconds)) < 0
                ? activeSession.ExpiresAtUtc
                : now.AddSeconds(options.Pairing.WebSocketTicketLifetimeSeconds);

            var ticket = CreateToken("wst_", 18);
            webSocketTickets[ticket] = new PendingWebSocketTicket(
                Ticket: ticket,
                SessionToken: activeSession.SessionToken,
                Origin: activeSession.Origin,
                ExpiresAtUtc: expiresAtUtc);

            return new WebSocketTicketResponse(
                Ticket: ticket,
                ExpiresAtUtc: expiresAtUtc);
        }
    }

    public SessionAuthorizationResult AuthorizeWebSocketTicket(string? ticket, string? origin)
    {
        lock (gate)
        {
            RemoveExpiredState();

            if (string.IsNullOrWhiteSpace(ticket))
            {
                return new SessionAuthorizationResult(false, null, "A wsTicket query value is required.", 401);
            }

            if (!webSocketTickets.TryGetValue(ticket, out var pendingTicket) ||
                pendingTicket.ExpiresAtUtc < GetUtcNow())
            {
                return new SessionAuthorizationResult(false, null, "The WebSocket ticket is invalid or expired.", 401);
            }

            if (!string.IsNullOrWhiteSpace(origin) &&
                !string.Equals(origin, pendingTicket.Origin, StringComparison.OrdinalIgnoreCase))
            {
                return new SessionAuthorizationResult(false, null, "The request origin does not match the active WebSocket ticket.", 403);
            }

            var authorization = AuthorizeSession(pendingTicket.SessionToken, pendingTicket.Origin);

            if (!authorization.Success)
            {
                return authorization;
            }

            webSocketTickets.Remove(ticket);
            return authorization;
        }
    }

    public string CreateStartupBanner(AgentRuntimeOptions runtimeOptions)
    {
        RotatePairingCodeIfNeededAndNotify();

        lock (gate)
        {
            var launchUrl = string.IsNullOrWhiteSpace(runtimeOptions.UiLaunch.Url)
                ? UiLaunchOptions.HostedProductionUrl.TrimEnd('/')
                : runtimeOptions.UiLaunch.Url;
            var launchFallbackNotice = ShouldMentionHostedFallback(launchUrl)
                ? $"{Environment.NewLine}If the local UI is not running, reopen {UiLaunchOptions.HostedProductionUrl} and enter this pairing code there."
                : string.Empty;

            return $"""
Kuberkynesis agent running on {runtimeOptions.PublicUrl}
Pairing code: {pairingCode}
Open {launchUrl} to connect{launchFallbackNotice}
Interactive sessions: 1
Read-only preview sessions: unlimited
""";
        }
    }

    public SessionResponse CreateSessionResponse(AuthenticatedAgentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return new SessionResponse(
            AgentInstanceId: agentInstanceId,
            SessionExpiresAtUtc: session.ExpiresAtUtc,
            GrantedMode: session.GrantedMode,
            Origin: session.Origin,
            AppVersion: session.AppVersion);
    }

    public string CreateUiLaunchUrl(string uiUrl, string agentPublicUrl, OriginAccessClassifier classifier, bool autoConnectWithPairingCode)
    {
        RotatePairingCodeIfNeededAndNotify();

        lock (gate)
        {
            if (!autoConnectWithPairingCode ||
                string.IsNullOrWhiteSpace(uiUrl) ||
                !Uri.TryCreate(uiUrl, UriKind.Absolute, out var uri))
            {
                return uiUrl;
            }

            var origin = uri.GetLeftPart(UriPartial.Authority);
            var decision = classifier.Evaluate(origin);

            if (!decision.IsAllowed)
            {
                return uiUrl;
            }

            var fragmentParts = new List<string>
            {
                $"kkPairingCode={Uri.EscapeDataString(pairingCode)}"
            };

            if (Uri.TryCreate(agentPublicUrl, UriKind.Absolute, out var publicUri))
            {
                fragmentParts.Add($"kkAgentUrl={Uri.EscapeDataString(publicUri.GetLeftPart(UriPartial.Authority) + "/")}");
            }

            var builder = new UriBuilder(uri)
            {
                Fragment = string.Join("&", fragmentParts)
            };

            return builder.Uri.AbsoluteUri;
        }
    }

    public void Dispose()
    {
        pairingCodeRotationTimer?.Dispose();
    }

    private void RemoveExpiredState()
    {
        var now = GetUtcNow();

        foreach (var key in nonces.Where(entry => entry.Value < now).Select(entry => entry.Key).ToArray())
        {
            nonces.Remove(key);
        }

        foreach (var key in webSocketTickets.Where(entry => entry.Value.ExpiresAtUtc < now).Select(entry => entry.Key).ToArray())
        {
            webSocketTickets.Remove(key);
        }

        foreach (var sessionToken in pendingSessionReleases
                     .Where(entry => entry.Value.ReleaseAtUtc <= now)
                     .Select(entry => entry.Key)
                     .ToArray())
        {
            RevokeSessionByToken(sessionToken);
        }

        foreach (var sessionToken in readonlySessions
                     .Where(session => session.ExpiresAtUtc < now)
                     .Select(session => session.SessionToken)
                     .ToArray())
        {
            RevokeSessionByToken(sessionToken);
        }

        if (interactiveSession?.ExpiresAtUtc < now)
        {
            RevokeSessionByToken(interactiveSession.SessionToken);
        }
    }

    private void HandlePairingCodeRotationTimerTick(object? _)
    {
        RotatePairingCodeIfNeededAndNotify(force: true);
    }

    private void RotatePairingCodeIfNeededAndNotify(bool force = false)
    {
        PairingCodeRotationNotice? notice;

        lock (gate)
        {
            notice = RotatePairingCodeIfNeededCore(force);
        }

        if (notice is not null)
        {
            PairingCodeRotated?.Invoke(notice);
        }
    }

    private PairingCodeRotationNotice? RotatePairingCodeIfNeededCore(bool force)
    {
        var now = GetUtcNow();

        if (!force && pairingCodeExpiresAtUtc > now)
        {
            return null;
        }

        pairingCode = CreatePairingCode(options.Pairing.CodeLength);
        pairingCodeExpiresAtUtc = now.AddMinutes(options.Pairing.PairingCodeRotationMinutes);
        SchedulePairingCodeRotationTimer();

        return new PairingCodeRotationNotice(pairingCode, pairingCodeExpiresAtUtc);
    }

    private void SchedulePairingCodeRotationTimer()
    {
        if (pairingCodeRotationTimer is null)
        {
            return;
        }

        var dueTime = pairingCodeExpiresAtUtc - GetUtcNow();

        if (dueTime < TimeSpan.Zero)
        {
            dueTime = TimeSpan.Zero;
        }

        pairingCodeRotationTimer.Change(dueTime, Timeout.InfiniteTimeSpan);
    }

    private DateTimeOffset GetUtcNow()
    {
        return timeProvider.GetUtcNow();
    }

    private ActiveSession? FindSession(string sessionToken)
    {
        if (interactiveSession?.SessionToken == sessionToken)
        {
            return interactiveSession;
        }

        return readonlySessions.FirstOrDefault(session => session.SessionToken == sessionToken);
    }

    private bool RevokeSessionByToken(string sessionToken)
    {
        var revoked = false;

        if (interactiveSession?.SessionToken == sessionToken)
        {
            interactiveSession = null;
            revoked = true;
        }

        revoked |= readonlySessions.RemoveAll(activeSession => activeSession.SessionToken == sessionToken) > 0;

        foreach (var key in webSocketTickets
                     .Where(entry => string.Equals(entry.Value.SessionToken, sessionToken, StringComparison.Ordinal))
                     .Select(entry => entry.Key)
                     .ToArray())
        {
            webSocketTickets.Remove(key);
        }

        pendingSessionReleases.Remove(sessionToken);
        return revoked;
    }

    private static string CreatePairingCode(int length)
    {
        var bytes = RandomNumberGenerator.GetBytes(length);
        Span<char> chars = stackalloc char[length];

        for (var index = 0; index < length; index++)
        {
            chars[index] = PairingAlphabet[bytes[index] % PairingAlphabet.Length];
        }

        return new string(chars);
    }

    private static string CreateToken(string prefix, int bytes)
    {
        return $"{prefix}{Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant()}";
    }

    private static bool ShouldMentionHostedFallback(string launchUrl)
    {
        return Uri.TryCreate(launchUrl, UriKind.Absolute, out var launchUri) &&
               launchUri.IsLoopback &&
               !string.Equals(
                   launchUri.GetLeftPart(UriPartial.Authority).TrimEnd('/') + "/",
                   UiLaunchOptions.HostedProductionUrl,
                   StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ActiveSession(
        string SessionToken,
        string CsrfToken,
        OriginAccessClass GrantedMode,
        string Origin,
        string AppVersion,
        DateTimeOffset ExpiresAtUtc)
    {
        public static ActiveSession Create(
            OriginAccessClass grantedMode,
            string origin,
            string appVersion,
            DateTimeOffset expiresAtUtc)
        {
            return new ActiveSession(
                SessionToken: CreateToken("pst_", 24),
                CsrfToken: CreateToken("csrf_", 18),
                GrantedMode: grantedMode,
                Origin: origin,
                AppVersion: appVersion,
                ExpiresAtUtc: expiresAtUtc);
        }
    }

    private sealed record PendingWebSocketTicket(
        string Ticket,
        string SessionToken,
        string Origin,
        DateTimeOffset ExpiresAtUtc);

    private sealed record PendingSessionRelease(
        string SessionToken,
        string Origin,
        DateTimeOffset ReleaseAtUtc);
}

public sealed record PairingCodeRotationNotice(
    string PairingCode,
    DateTimeOffset ExpiresAtUtc);
