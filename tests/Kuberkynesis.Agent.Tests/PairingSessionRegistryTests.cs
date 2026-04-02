using System.Text.RegularExpressions;
using System.Threading;
using Kuberkynesis.Agent.Core.Configuration;
using Kuberkynesis.Agent.Core.Security;
using Kuberkynesis.Ui.Shared.Connection;

namespace Kuberkynesis.Agent.Tests;

public sealed class PairingSessionRegistryTests
{
    private static readonly DateTimeOffset BaselineUtc = new(2026, 3, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AuthorizeSession_AllowsMatchingOriginAndRejectsMismatchedOrigin()
    {
        var options = new AgentRuntimeOptions
        {
            Origins = new OriginOptions
            {
                Interactive = ["https://app.kuberkynesis.com"]
            }
        };
        var classifier = new OriginAccessClassifier(options);
        var registry = new PairingSessionRegistry(options);
        var banner = registry.CreateStartupBanner(options);
        var pairingCode = Regex.Match(banner, "Pairing code: (?<code>[A-Z0-9]+)").Groups["code"].Value;
        var hello = registry.CreateHelloResponse(classifier);

        var pairResult = registry.TryPair(
            new PairRequest
            {
                Nonce = hello.Nonce,
                AppVersion = "0.1.0",
                PairingCode = pairingCode,
                Origin = "https://app.kuberkynesis.com",
                RequestedMode = OriginAccessClass.Interactive
            },
            "https://app.kuberkynesis.com",
            classifier.Evaluate("https://app.kuberkynesis.com"));

        Assert.True(pairResult.Success);
        Assert.NotNull(pairResult.Response);

        var authorized = registry.AuthorizeSession(pairResult.Response!.SessionToken, "https://app.kuberkynesis.com");
        var rejected = registry.AuthorizeSession(pairResult.Response.SessionToken, "https://evil.example");

        Assert.True(authorized.Success);
        Assert.Equal(OriginAccessClass.Interactive, authorized.Session?.GrantedMode);
        Assert.False(rejected.Success);
        Assert.Equal(403, rejected.StatusCode);
    }

    [Fact]
    public void CreateUiLaunchUrl_EmbedsPairingCodeForAllowedOrigin()
    {
        var options = new AgentRuntimeOptions
        {
            Origins = new OriginOptions
            {
                Interactive = ["http://localhost:5173"]
            },
            UiLaunch = new UiLaunchOptions
            {
                Url = "http://localhost:5173/"
            }
        };

        var classifier = new OriginAccessClassifier(options);
        var registry = new PairingSessionRegistry(options);
        var banner = registry.CreateStartupBanner(options);
        var pairingCode = Regex.Match(banner, "Pairing code: (?<code>[A-Z0-9]+)").Groups["code"].Value;

        var launchUrl = registry.CreateUiLaunchUrl(options.UiLaunch.Url, options.PublicUrl, classifier, autoConnectWithPairingCode: true);

        Assert.Contains("kkPairingCode=", launchUrl, StringComparison.Ordinal);
        Assert.Contains($"kkPairingCode={pairingCode}", launchUrl, StringComparison.Ordinal);
        Assert.Contains("kkAgentUrl=", launchUrl, StringComparison.Ordinal);
        Assert.Contains(Uri.EscapeDataString("http://127.0.0.1:46321/"), launchUrl, StringComparison.Ordinal);
        Assert.DoesNotContain("kkAutoConnect=1", launchUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void TryPair_GrantsReadonlyPreviewSessionForPreviewOrigins()
    {
        var options = new AgentRuntimeOptions
        {
            Origins = new OriginOptions
            {
                Interactive = ["http://localhost:5173"],
                PreviewPattern = "^https://[a-z0-9-]+\\.kuberkynesis-ui\\.pages\\.dev$"
            }
        };

        const string previewOrigin = "https://lab-preview.kuberkynesis-ui.pages.dev";
        var classifier = new OriginAccessClassifier(options);
        var registry = new PairingSessionRegistry(options);
        var hello = registry.CreateHelloResponse(classifier);
        var pairingCode = ExtractPairingCode(registry, options);

        var result = registry.TryPair(
            new PairRequest
            {
                Nonce = hello.Nonce,
                AppVersion = "1.0.0",
                PairingCode = pairingCode,
                Origin = previewOrigin,
                RequestedMode = OriginAccessClass.Interactive
            },
            previewOrigin,
            classifier.Evaluate(previewOrigin));

        Assert.True(result.Success);
        Assert.Equal(OriginAccessClass.ReadonlyPreview, result.Response?.GrantedMode);
    }

    [Fact]
    public void CreateHelloResponse_UsesAdvertisedVersionOverrideWhenConfigured()
    {
        var options = new AgentRuntimeOptions
        {
            AdvertisedVersionOverride = "9.9.9-test",
            Origins = new OriginOptions
            {
                Interactive = ["http://localhost:5173"]
            }
        };

        var classifier = new OriginAccessClassifier(options);
        var registry = new PairingSessionRegistry(options);

        var hello = registry.CreateHelloResponse(classifier);

        Assert.Equal("9.9.9-test", hello.AgentVersion);
    }

    [Fact]
    public void CreateSessionResponse_ReturnsTheAuthorizedSessionMetadata()
    {
        var options = new AgentRuntimeOptions
        {
            Origins = new OriginOptions
            {
                Interactive = ["http://localhost:5173"]
            }
        };

        var classifier = new OriginAccessClassifier(options);
        var registry = new PairingSessionRegistry(options);
        var banner = registry.CreateStartupBanner(options);
        var pairingCode = Regex.Match(banner, "Pairing code: (?<code>[A-Z0-9]+)").Groups["code"].Value;
        var hello = registry.CreateHelloResponse(classifier);

        var pairResult = registry.TryPair(
            new PairRequest
            {
                Nonce = hello.Nonce,
                AppVersion = "1.0.0",
                PairingCode = pairingCode,
                Origin = "http://localhost:5173",
                RequestedMode = OriginAccessClass.Interactive
            },
            "http://localhost:5173",
            classifier.Evaluate("http://localhost:5173"));

        var authorization = registry.AuthorizeSession(pairResult.Response!.SessionToken, "http://localhost:5173");
        var session = registry.CreateSessionResponse(authorization.Session!);

        Assert.Equal(registry.AgentInstanceId, session.AgentInstanceId);
        Assert.Equal(OriginAccessClass.Interactive, session.GrantedMode);
        Assert.Equal("http://localhost:5173", session.Origin);
        Assert.Equal("1.0.0", session.AppVersion);
    }

    [Fact]
    public void CreateWebSocketTicket_ReturnsAOneTimeOriginBoundTicket()
    {
        var options = new AgentRuntimeOptions
        {
            Origins = new OriginOptions
            {
                Interactive = ["http://localhost:5173"]
            }
        };

        var classifier = new OriginAccessClassifier(options);
        var registry = new PairingSessionRegistry(options);
        var hello = registry.CreateHelloResponse(classifier);
        var pairingCode = ExtractPairingCode(registry, options);
        var pairResult = registry.TryPair(
            new PairRequest
            {
                Nonce = hello.Nonce,
                AppVersion = "1.0.0",
                PairingCode = pairingCode,
                Origin = "http://localhost:5173",
                RequestedMode = OriginAccessClass.Interactive
            },
            "http://localhost:5173",
            classifier.Evaluate("http://localhost:5173"));

        var session = registry.AuthorizeSession(pairResult.Response!.SessionToken, "http://localhost:5173").Session!;
        var ticket = registry.CreateWebSocketTicket(session);
        var firstAuthorization = registry.AuthorizeWebSocketTicket(ticket.Ticket, "http://localhost:5173");
        var secondAuthorization = registry.AuthorizeWebSocketTicket(ticket.Ticket, "http://localhost:5173");

        Assert.True(firstAuthorization.Success);
        Assert.Equal("http://localhost:5173", firstAuthorization.Session?.Origin);
        Assert.False(secondAuthorization.Success);
        Assert.Equal(401, secondAuthorization.StatusCode);
    }

    [Fact]
    public void AuthorizeWebSocketTicket_RejectsMismatchedOriginsWithoutConsumingTheTicket()
    {
        var options = new AgentRuntimeOptions
        {
            Origins = new OriginOptions
            {
                Interactive = ["http://localhost:5173"]
            }
        };

        var classifier = new OriginAccessClassifier(options);
        var registry = new PairingSessionRegistry(options);
        var hello = registry.CreateHelloResponse(classifier);
        var pairingCode = ExtractPairingCode(registry, options);
        var pairResult = registry.TryPair(
            new PairRequest
            {
                Nonce = hello.Nonce,
                AppVersion = "1.0.0",
                PairingCode = pairingCode,
                Origin = "http://localhost:5173",
                RequestedMode = OriginAccessClass.Interactive
            },
            "http://localhost:5173",
            classifier.Evaluate("http://localhost:5173"));

        var session = registry.AuthorizeSession(pairResult.Response!.SessionToken, "http://localhost:5173").Session!;
        var ticket = registry.CreateWebSocketTicket(session);
        var rejected = registry.AuthorizeWebSocketTicket(ticket.Ticket, "https://evil.example");
        var authorized = registry.AuthorizeWebSocketTicket(ticket.Ticket, "http://localhost:5173");

        Assert.False(rejected.Success);
        Assert.Equal(403, rejected.StatusCode);
        Assert.True(authorized.Success);
    }

    [Fact]
    public void TryPair_RejectsExpiredNonces()
    {
        var options = new AgentRuntimeOptions
        {
            Origins = new OriginOptions
            {
                Interactive = ["http://localhost:5173"]
            },
            Pairing = new PairingOptions
            {
                NonceLifetimeSeconds = 60
            }
        };
        var clock = new FakeTimeProvider(BaselineUtc);
        var classifier = new OriginAccessClassifier(options);
        var registry = new PairingSessionRegistry(options, clock);
        var hello = registry.CreateHelloResponse(classifier);
        var pairingCode = ExtractPairingCode(registry, options);
        clock.Advance(TimeSpan.FromSeconds(61));

        var result = registry.TryPair(
            new PairRequest
            {
                Nonce = hello.Nonce,
                AppVersion = "1.0.0",
                PairingCode = pairingCode,
                Origin = "http://localhost:5173",
                RequestedMode = OriginAccessClass.Interactive
            },
            "http://localhost:5173",
            classifier.Evaluate("http://localhost:5173"));

        Assert.False(result.Success);
        Assert.Equal(400, result.StatusCode);
        Assert.Contains("nonce", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateHelloResponse_RotatesThePairingCodeAfterTheConfiguredWindow()
    {
        var options = new AgentRuntimeOptions
        {
            Origins = new OriginOptions
            {
                Interactive = ["http://localhost:5173"]
            },
            Pairing = new PairingOptions
            {
                PairingCodeRotationMinutes = 10
            }
        };
        var clock = new FakeTimeProvider(BaselineUtc);
        var classifier = new OriginAccessClassifier(options);
        var registry = new PairingSessionRegistry(options, clock);
        var firstCode = ExtractPairingCode(registry, options);

        clock.Advance(TimeSpan.FromMinutes(11));
        _ = registry.CreateHelloResponse(classifier);
        var rotatedCode = ExtractPairingCode(registry, options);

        Assert.NotEqual(firstCode, rotatedCode);
    }

    [Fact]
    public void PairingCodeRotationTimer_RotatesTheCodeAndRaisesANoticeWithoutARequest()
    {
        var options = new AgentRuntimeOptions
        {
            Origins = new OriginOptions
            {
                Interactive = ["http://localhost:5173"]
            },
            Pairing = new PairingOptions
            {
                PairingCodeRotationMinutes = 10
            }
        };
        var clock = new FakeTimeProvider(BaselineUtc);
        using var registry = new PairingSessionRegistry(options, clock);
        var firstCode = ExtractPairingCode(registry, options);
        PairingCodeRotationNotice? notice = null;

        registry.PairingCodeRotated += rotationNotice => notice = rotationNotice;

        clock.Advance(TimeSpan.FromMinutes(10));

        var rotatedCode = ExtractPairingCode(registry, options);

        Assert.NotNull(notice);
        Assert.NotEqual(firstCode, rotatedCode);
        Assert.Equal(rotatedCode, notice!.PairingCode);
    }

    [Fact]
    public void RevokeSession_ReleasesTheInteractiveSlotForAPrimaryReconnect()
    {
        var options = new AgentRuntimeOptions
        {
            Origins = new OriginOptions
            {
                Interactive = ["http://localhost:5173"]
            }
        };

        var classifier = new OriginAccessClassifier(options);
        using var registry = new PairingSessionRegistry(options);
        var firstHello = registry.CreateHelloResponse(classifier);
        var pairingCode = ExtractPairingCode(registry, options);
        var firstPair = registry.TryPair(
            new PairRequest
            {
                Nonce = firstHello.Nonce,
                AppVersion = "1.0.0",
                PairingCode = pairingCode,
                Origin = "http://localhost:5173",
                RequestedMode = OriginAccessClass.Interactive
            },
            "http://localhost:5173",
            classifier.Evaluate("http://localhost:5173"));

        Assert.True(firstPair.Success);

        var authorizedSession = registry.AuthorizeSession(firstPair.Response!.SessionToken, "http://localhost:5173");
        Assert.True(authorizedSession.Success);

        Assert.True(registry.RevokeSession(authorizedSession.Session!));
        Assert.False(registry.AuthorizeSession(firstPair.Response.SessionToken, "http://localhost:5173").Success);

        var secondHello = registry.CreateHelloResponse(classifier);
        var secondPair = registry.TryPair(
            new PairRequest
            {
                Nonce = secondHello.Nonce,
                AppVersion = "1.0.0",
                PairingCode = pairingCode,
                Origin = "http://localhost:5173",
                RequestedMode = OriginAccessClass.Interactive
            },
            "http://localhost:5173",
            classifier.Evaluate("http://localhost:5173"));

        Assert.True(secondPair.Success);
        Assert.NotEqual(firstPair.Response.SessionToken, secondPair.Response!.SessionToken);
    }

    [Fact]
    public void TryPair_RejectsASecondInteractiveSessionWhileOneIsActive()
    {
        var options = new AgentRuntimeOptions
        {
            Origins = new OriginOptions
            {
                Interactive = ["http://localhost:5173"]
            }
        };
        var classifier = new OriginAccessClassifier(options);
        var registry = new PairingSessionRegistry(options, new FakeTimeProvider(BaselineUtc));
        var firstHello = registry.CreateHelloResponse(classifier);
        var pairingCode = ExtractPairingCode(registry, options);

        var firstPair = registry.TryPair(
            new PairRequest
            {
                Nonce = firstHello.Nonce,
                AppVersion = "1.0.0",
                PairingCode = pairingCode,
                Origin = "http://localhost:5173",
                RequestedMode = OriginAccessClass.Interactive
            },
            "http://localhost:5173",
            classifier.Evaluate("http://localhost:5173"));

        Assert.True(firstPair.Success);

        var secondHello = registry.CreateHelloResponse(classifier);
        var secondPair = registry.TryPair(
            new PairRequest
            {
                Nonce = secondHello.Nonce,
                AppVersion = "1.0.0",
                PairingCode = pairingCode,
                Origin = "http://localhost:5173",
                RequestedMode = OriginAccessClass.Interactive
            },
            "http://localhost:5173",
            classifier.Evaluate("http://localhost:5173"));

        Assert.False(secondPair.Success);
        Assert.Equal(409, secondPair.StatusCode);
        Assert.Contains("Interactive control is already held", secondPair.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void TryPair_AllowsExplicitTakeoverOfTheExistingInteractiveSession()
    {
        var options = new AgentRuntimeOptions
        {
            Origins = new OriginOptions
            {
                Interactive = ["http://localhost:5173"]
            }
        };
        var classifier = new OriginAccessClassifier(options);
        var registry = new PairingSessionRegistry(options, new FakeTimeProvider(BaselineUtc));
        var firstHello = registry.CreateHelloResponse(classifier);
        var pairingCode = ExtractPairingCode(registry, options);

        var firstPair = registry.TryPair(
            new PairRequest
            {
                Nonce = firstHello.Nonce,
                AppVersion = "1.0.0",
                PairingCode = pairingCode,
                Origin = "http://localhost:5173",
                RequestedMode = OriginAccessClass.Interactive
            },
            "http://localhost:5173",
            classifier.Evaluate("http://localhost:5173"));

        Assert.True(firstPair.Success);

        var secondHello = registry.CreateHelloResponse(classifier);
        var secondPair = registry.TryPair(
            new PairRequest
            {
                Nonce = secondHello.Nonce,
                AppVersion = "1.0.0",
                PairingCode = pairingCode,
                Origin = "http://localhost:5173",
                RequestedMode = OriginAccessClass.Interactive,
                TakeoverInteractiveSession = true
            },
            "http://localhost:5173",
            classifier.Evaluate("http://localhost:5173"));

        Assert.True(secondPair.Success);
        Assert.NotEqual(firstPair.Response!.SessionToken, secondPair.Response!.SessionToken);
        Assert.False(registry.AuthorizeSession(firstPair.Response.SessionToken, "http://localhost:5173").Success);
        Assert.True(registry.AuthorizeSession(secondPair.Response.SessionToken, "http://localhost:5173").Success);
    }

    [Fact]
    public void ScheduleSessionRelease_AllowsImmediateRePairingWithoutRestartingTheAgent()
    {
        var options = new AgentRuntimeOptions
        {
            Origins = new OriginOptions
            {
                Interactive = ["http://localhost:5173"]
            }
        };
        var classifier = new OriginAccessClassifier(options);
        var registry = new PairingSessionRegistry(options, new FakeTimeProvider(BaselineUtc));
        var pairingCode = ExtractPairingCode(registry, options);
        var firstHello = registry.CreateHelloResponse(classifier);

        var firstPair = registry.TryPair(
            new PairRequest
            {
                Nonce = firstHello.Nonce,
                AppVersion = "1.0.0",
                PairingCode = pairingCode,
                Origin = "http://localhost:5173",
                RequestedMode = OriginAccessClass.Interactive
            },
            "http://localhost:5173",
            classifier.Evaluate("http://localhost:5173"));

        Assert.True(firstPair.Success);
        Assert.True(registry.ScheduleSessionRelease(firstPair.Response!.SessionToken, "http://localhost:5173"));

        var secondHello = registry.CreateHelloResponse(classifier);
        var secondPair = registry.TryPair(
            new PairRequest
            {
                Nonce = secondHello.Nonce,
                AppVersion = "1.0.0",
                PairingCode = pairingCode,
                Origin = "http://localhost:5173",
                RequestedMode = OriginAccessClass.Interactive
            },
            "http://localhost:5173",
            classifier.Evaluate("http://localhost:5173"));

        Assert.True(secondPair.Success);
        Assert.NotEqual(firstPair.Response.SessionToken, secondPair.Response!.SessionToken);
        Assert.False(registry.AuthorizeSession(firstPair.Response.SessionToken, "http://localhost:5173").Success);
    }

    [Fact]
    public void AuthorizeSession_CancelsAPendingReleaseSoRefreshDoesNotDropTheSession()
    {
        var options = new AgentRuntimeOptions
        {
            Origins = new OriginOptions
            {
                Interactive = ["http://localhost:5173"]
            },
            Pairing = new PairingOptions
            {
                DisconnectReleaseGraceSeconds = 5
            }
        };
        var clock = new FakeTimeProvider(BaselineUtc);
        var classifier = new OriginAccessClassifier(options);
        var registry = new PairingSessionRegistry(options, clock);
        var pairingCode = ExtractPairingCode(registry, options);
        var hello = registry.CreateHelloResponse(classifier);

        var pair = registry.TryPair(
            new PairRequest
            {
                Nonce = hello.Nonce,
                AppVersion = "1.0.0",
                PairingCode = pairingCode,
                Origin = "http://localhost:5173",
                RequestedMode = OriginAccessClass.Interactive
            },
            "http://localhost:5173",
            classifier.Evaluate("http://localhost:5173"));

        Assert.True(pair.Success);
        Assert.True(registry.ScheduleSessionRelease(pair.Response!.SessionToken, "http://localhost:5173"));

        var refreshedSession = registry.AuthorizeSession(pair.Response.SessionToken, "http://localhost:5173");
        Assert.True(refreshedSession.Success);

        clock.Advance(TimeSpan.FromSeconds(6));

        var afterGrace = registry.AuthorizeSession(pair.Response.SessionToken, "http://localhost:5173");
        Assert.True(afterGrace.Success);
    }

    private static string ExtractPairingCode(PairingSessionRegistry registry, AgentRuntimeOptions options)
    {
        var banner = registry.CreateStartupBanner(options);
        return Regex.Match(banner, "Pairing code: (?<code>[A-Z0-9]+)").Groups["code"].Value;
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset utcNow;
        private readonly List<FakeTimer> timers = [];

        public FakeTimeProvider(DateTimeOffset utcNow)
        {
            this.utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return utcNow;
        }

        public void Advance(TimeSpan delta)
        {
            utcNow = utcNow.Add(delta);

            foreach (var timer in timers.ToArray())
            {
                timer.TryFire(utcNow);
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new FakeTimer(this, callback, state, utcNow, dueTime, period);
            timers.Add(timer);
            return timer;
        }

        private sealed class FakeTimer : ITimer
        {
            private readonly FakeTimeProvider owner;
            private readonly TimerCallback callback;
            private readonly object? state;
            private bool disposed;
            private DateTimeOffset? dueAtUtc;
            private TimeSpan period;

            public FakeTimer(
                FakeTimeProvider owner,
                TimerCallback callback,
                object? state,
                DateTimeOffset nowUtc,
                TimeSpan dueTime,
                TimeSpan period)
            {
                this.owner = owner;
                this.callback = callback;
                this.state = state;
                ChangeCore(nowUtc, dueTime, period);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (disposed)
                {
                    return false;
                }

                ChangeCore(owner.utcNow, dueTime, period);
                return true;
            }

            public void Dispose()
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                owner.timers.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public void TryFire(DateTimeOffset nowUtc)
            {
                if (disposed || dueAtUtc is null || nowUtc < dueAtUtc.Value)
                {
                    return;
                }

                callback(state);

                if (disposed)
                {
                    return;
                }

                if (period == Timeout.InfiniteTimeSpan)
                {
                    dueAtUtc = null;
                    return;
                }

                dueAtUtc = nowUtc.Add(period);
            }

            private void ChangeCore(DateTimeOffset nowUtc, TimeSpan dueTime, TimeSpan period)
            {
                this.period = period;

                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    dueAtUtc = null;
                    return;
                }

                if (dueTime < TimeSpan.Zero)
                {
                    dueTime = TimeSpan.Zero;
                }

                dueAtUtc = nowUtc.Add(dueTime);
            }
        }
    }
}
