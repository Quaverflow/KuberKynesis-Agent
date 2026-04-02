using System.Text;
using System.Text.Json;
using Kuberkynesis.Agent.Core.Security;
using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Kube;

public sealed class KubePodExecSessionCoordinator
{
    private static readonly TimeSpan CompletedSessionRetention = TimeSpan.FromMinutes(10);
    private const int HttpOk = 200;
    private const int HttpNoContent = 204;
    private const int HttpForbidden = 403;
    private const int HttpNotFound = 404;
    private const int HttpConflict = 409;
    private const int OutputFrameLimit = 400;

    private readonly object gate = new();
    private readonly IKubePodExecRuntimeFactory runtimeFactory;
    private readonly TimeProvider timeProvider;
    private readonly Dictionary<string, PodExecSession> sessions = new(StringComparer.Ordinal);

    public KubePodExecSessionCoordinator(
        IKubePodExecRuntimeFactory runtimeFactory,
        TimeProvider? timeProvider = null)
    {
        this.runtimeFactory = runtimeFactory;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<KubePodExecStartResponse> StartSessionAsync(
        AuthenticatedAgentSession session,
        KubePodExecStartRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);

        CleanupExpiredSessions();

        var sessionId = $"pex_{Guid.NewGuid():N}";
        var startedAtUtc = GetUtcNow();
        var resource = new KubeResourceIdentity(request.ContextName.Trim(), KubeResourceKind.Pod, request.Namespace.Trim(), request.PodName.Trim());
        var execSession = new PodExecSession(
            sessionId,
            session.SessionToken,
            request,
            new KubePodExecSessionSnapshot(
                SessionId: sessionId,
                Resource: resource,
                ContainerName: string.IsNullOrWhiteSpace(request.ContainerName) ? null : request.ContainerName.Trim(),
                Command: request.Command,
                UpdatedAtUtc: startedAtUtc,
                StatusText: "Opening shell",
                Summary: "The local agent is opening a Kubernetes exec session for the selected pod.",
                OutputFrames: []));

        lock (gate)
        {
            sessions[sessionId] = execSession;
        }

        try
        {
            var runtime = await runtimeFactory.CreateAsync(request, cancellationToken);
            execSession.AttachRuntime(runtime, startedAtUtc);
            _ = Task.Run(() => RunSessionAsync(execSession), CancellationToken.None);

            return new KubePodExecStartResponse(
                SessionId: sessionId,
                Resource: runtime.Resource,
                ContainerName: runtime.ContainerName,
                Command: runtime.Command,
                StartedAtUtc: startedAtUtc,
                StatusText: "Shell ready",
                Summary: $"Interactive exec is attached to {runtime.Resource.Name}. Send lines below to drive the shell.",
                TransparencyCommands: runtime.TransparencyCommands)
            {
                CanCancel = true,
                CanSendInput = true
            };
        }
        catch
        {
            lock (gate)
            {
                sessions.Remove(sessionId);
            }

            throw;
        }
    }

    public async Task<KubePodExecInputResult> SendInputAsync(
        AuthenticatedAgentSession session,
        string sessionId,
        KubePodExecInputRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        if (!TryGetAuthorizedSession(sessionId, session, out var execSession, out var statusCode, out var errorMessage))
        {
            return new KubePodExecInputResult(false, statusCode, errorMessage);
        }

        return await execSession!.SendInputAsync(request, GetUtcNow(), cancellationToken);
    }

    public KubePodExecCancelResult CancelSession(
        AuthenticatedAgentSession session,
        string sessionId)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        if (!TryGetAuthorizedSession(sessionId, session, out var execSession, out var statusCode, out var errorMessage))
        {
            return new KubePodExecCancelResult(false, statusCode, errorMessage);
        }

        return execSession!.RequestCancellation(GetUtcNow());
    }

    public bool TryAuthorizeSession(
        string sessionId,
        AuthenticatedAgentSession session,
        out int statusCode,
        out string? errorMessage)
    {
        return TryGetAuthorizedSession(sessionId, session, out _, out statusCode, out errorMessage);
    }

    public async Task StreamAsync(
        string sessionId,
        AuthenticatedAgentSession session,
        Func<KubePodExecStreamMessage, CancellationToken, Task> onMessage,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(onMessage);

        if (!TryGetAuthorizedSession(sessionId, session, out var execSession, out var statusCode, out var errorMessage))
        {
            throw CreateStreamAuthorizationException(statusCode, errorMessage);
        }

        var sentCount = 0;

        while (true)
        {
            var batch = execSession!.ReadFrom(sentCount);

            if (batch.Messages.Count > 0)
            {
                sentCount += batch.Messages.Count;

                foreach (var message in batch.Messages)
                {
                    await onMessage(message, cancellationToken);
                }

                if (batch.IsTerminal)
                {
                    break;
                }

                continue;
            }

            if (batch.IsTerminal)
            {
                break;
            }

            await batch.WaitForUpdate.WaitAsync(cancellationToken);
        }
    }

    private async Task RunSessionAsync(PodExecSession execSession)
    {
        try
        {
            execSession.PublishSnapshot(
                "Shell ready",
                "Interactive exec is attached. Use the input below to send lines to the remote shell.",
                GetUtcNow(),
                canCancel: true,
                canSendInput: true);

            var statusText = new StringBuilder();
            var linkedCancellation = execSession.ExecutionCancellation.Token;
            var runtime = execSession.Runtime
                ?? throw new InvalidOperationException("The exec runtime was not attached before the session started.");

            var stdoutPump = PumpOutputAsync(runtime.StdOut, KubePodExecOutputChannel.StdOut, execSession, linkedCancellation);
            var stderrPump = PumpOutputAsync(runtime.StdErr, KubePodExecOutputChannel.StdErr, execSession, linkedCancellation);
            var statusPump = PumpStatusAsync(runtime.Status, statusText, linkedCancellation);

            await Task.WhenAll(stdoutPump, stderrPump, statusPump);

            var guidance = ParseExecStatusGuidance(statusText.ToString());

            if (execSession.ExecutionCancellation.IsCancellationRequested)
            {
                execSession.PublishCancelled(
                    GetUtcNow(),
                    guidance ?? "The browser requested that the local agent close the remote shell.");
            }
            else
            {
                execSession.PublishCompleted(
                    GetUtcNow(),
                    guidance ?? "The remote shell closed.");
            }
        }
        catch (OperationCanceledException) when (execSession.ExecutionCancellation.IsCancellationRequested)
        {
            execSession.PublishCancelled(
                GetUtcNow(),
                "The browser requested that the local agent close the remote shell.");
        }
        catch (Exception exception)
        {
            execSession.PublishError(
                exception.Message,
                CreateExecErrorGuidance(exception),
                GetUtcNow());
        }
        finally
        {
            await execSession.DisposeRuntimeAsync();
        }
    }

    private static async Task PumpOutputAsync(
        Stream stream,
        KubePodExecOutputChannel channel,
        PodExecSession execSession,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[1024];

        while (!cancellationToken.IsCancellationRequested)
        {
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);

            if (bytesRead <= 0)
            {
                break;
            }

            var text = Encoding.UTF8.GetString(buffer, 0, bytesRead);

            if (text.Length is 0)
            {
                continue;
            }

            execSession.PublishOutput(
                new KubePodExecOutputFrame(
                    OccurredAtUtc: DateTimeOffset.UtcNow,
                    Channel: channel,
                    Text: text));
        }
    }

    private static async Task PumpStatusAsync(
        Stream stream,
        StringBuilder builder,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[512];

        while (!cancellationToken.IsCancellationRequested)
        {
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);

            if (bytesRead <= 0)
            {
                break;
            }

            builder.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));
        }
    }

    private bool TryGetAuthorizedSession(
        string sessionId,
        AuthenticatedAgentSession session,
        out PodExecSession? execSession,
        out int statusCode,
        out string? errorMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(session);

        CleanupExpiredSessions();

        lock (gate)
        {
            if (!sessions.TryGetValue(sessionId, out execSession))
            {
                statusCode = HttpNotFound;
                errorMessage = "The requested exec session is no longer available.";
                return false;
            }

            if (!string.Equals(execSession.OwnerSessionToken, session.SessionToken, StringComparison.Ordinal))
            {
                execSession = null;
                statusCode = HttpForbidden;
                errorMessage = "This exec session belongs to a different browser session.";
                return false;
            }

            statusCode = HttpOk;
            errorMessage = null;
            return true;
        }
    }

    private void CleanupExpiredSessions()
    {
        var cutoffUtc = GetUtcNow().Subtract(CompletedSessionRetention);

        lock (gate)
        {
            foreach (var sessionId in sessions
                         .Where(static pair => pair.Value.CanExpire)
                         .Where(pair => pair.Value.TerminalAtUtc <= cutoffUtc)
                         .Select(static pair => pair.Key)
                         .ToArray())
            {
                sessions.Remove(sessionId);
            }
        }
    }

    private DateTimeOffset GetUtcNow() => timeProvider.GetUtcNow();

    private static Exception CreateStreamAuthorizationException(int statusCode, string? errorMessage)
    {
        return statusCode switch
        {
            HttpNotFound => new KeyNotFoundException(errorMessage),
            _ => new UnauthorizedAccessException(errorMessage)
        };
    }

    private static string? ParseExecStatusGuidance(string rawStatus)
    {
        if (string.IsNullOrWhiteSpace(rawStatus))
        {
            return null;
        }

        var trimmed = rawStatus.Trim();

        if (!trimmed.StartsWith('{'))
        {
            return trimmed;
        }

        try
        {
            using var document = JsonDocument.Parse(trimmed);

            if (document.RootElement.TryGetProperty("message", out var messageElement) &&
                messageElement.ValueKind is JsonValueKind.String)
            {
                return messageElement.GetString();
            }
        }
        catch
        {
        }

        return trimmed;
    }

    private static string CreateExecErrorGuidance(Exception exception)
    {
        return exception switch
        {
            InvalidOperationException => "Choose a different container or shell command, then try the exec session again.",
            ArgumentException => "Adjust the target pod, namespace, container, or command before retrying the exec session.",
            System.Net.WebSockets.WebSocketException => "The exec transport closed unexpectedly. Reopen the shell if you still need an interactive session.",
            _ => "Reopen the shell once the selected pod is healthy and reachable again."
        };
    }

    private sealed class PodExecSession
    {
        private readonly object gate = new();
        private readonly List<KubePodExecStreamMessage> messages = [];
        private readonly List<KubePodExecOutputFrame> outputFrames = [];
        private TaskCompletionSource<bool> nextUpdate = CreateSignal();

        public PodExecSession(
            string sessionId,
            string ownerSessionToken,
            KubePodExecStartRequest request,
            KubePodExecSessionSnapshot initialSnapshot)
        {
            SessionId = sessionId;
            OwnerSessionToken = ownerSessionToken;
            Request = request;
            ExecutionCancellation = new CancellationTokenSource();

            AddMessage(
                new KubePodExecStreamMessage(
                    MessageType: KubePodExecStreamMessageType.Snapshot,
                    OccurredAtUtc: initialSnapshot.UpdatedAtUtc,
                    SessionId: sessionId,
                    Snapshot: initialSnapshot,
                    OutputFrame: null,
                    ErrorMessage: null));
        }

        public string SessionId { get; }

        public string OwnerSessionToken { get; }

        public KubePodExecStartRequest Request { get; }

        public IKubePodExecRuntime? Runtime { get; private set; }

        public CancellationTokenSource ExecutionCancellation { get; }

        public DateTimeOffset? TerminalAtUtc { get; private set; }

        public bool CanExpire => TerminalAtUtc.HasValue;

        public void AttachRuntime(IKubePodExecRuntime runtime, DateTimeOffset startedAtUtc)
        {
            ArgumentNullException.ThrowIfNull(runtime);

            lock (gate)
            {
                Runtime = runtime;
                AddMessage(
                    new KubePodExecStreamMessage(
                        MessageType: KubePodExecStreamMessageType.Snapshot,
                        OccurredAtUtc: startedAtUtc,
                        SessionId: SessionId,
                        Snapshot: new KubePodExecSessionSnapshot(
                            SessionId,
                            runtime.Resource,
                            runtime.ContainerName,
                            runtime.Command,
                            startedAtUtc,
                            StatusText: "Opening shell",
                            Summary: "The local agent opened the Kubernetes exec socket and is waiting for the browser to subscribe.",
                            OutputFrames: outputFrames.ToArray())
                        {
                            CanCancel = true,
                            CanSendInput = true
                        },
                        OutputFrame: null,
                        ErrorMessage: null));
            }
        }

        public void PublishSnapshot(
            string statusText,
            string summary,
            DateTimeOffset occurredAtUtc,
            bool canCancel,
            bool canSendInput)
        {
            lock (gate)
            {
                if (TerminalAtUtc.HasValue || Runtime is null)
                {
                    return;
                }

                AddMessage(
                    new KubePodExecStreamMessage(
                        MessageType: KubePodExecStreamMessageType.Snapshot,
                        OccurredAtUtc: occurredAtUtc,
                        SessionId: SessionId,
                        Snapshot: new KubePodExecSessionSnapshot(
                            SessionId,
                            Runtime.Resource,
                            Runtime.ContainerName,
                            Runtime.Command,
                            occurredAtUtc,
                            statusText,
                            summary,
                            outputFrames.ToArray())
                        {
                            CanCancel = canCancel,
                            CanSendInput = canSendInput
                        },
                        OutputFrame: null,
                        ErrorMessage: null));
            }
        }

        public void PublishOutput(KubePodExecOutputFrame frame)
        {
            ArgumentNullException.ThrowIfNull(frame);

            lock (gate)
            {
                if (TerminalAtUtc.HasValue)
                {
                    return;
                }

                outputFrames.Add(frame);

                while (outputFrames.Count > OutputFrameLimit)
                {
                    outputFrames.RemoveAt(0);
                }

                AddMessage(
                    new KubePodExecStreamMessage(
                        MessageType: KubePodExecStreamMessageType.Output,
                        OccurredAtUtc: frame.OccurredAtUtc,
                        SessionId: SessionId,
                        Snapshot: null,
                        OutputFrame: frame,
                        ErrorMessage: null));
            }
        }

        public void PublishCompleted(DateTimeOffset occurredAtUtc, string summary)
        {
            lock (gate)
            {
                if (TerminalAtUtc.HasValue || Runtime is null)
                {
                    return;
                }

                TerminalAtUtc = occurredAtUtc;
                AddMessage(
                    new KubePodExecStreamMessage(
                        MessageType: KubePodExecStreamMessageType.Completed,
                        OccurredAtUtc: occurredAtUtc,
                        SessionId: SessionId,
                        Snapshot: new KubePodExecSessionSnapshot(
                            SessionId,
                            Runtime.Resource,
                            Runtime.ContainerName,
                            Runtime.Command,
                            occurredAtUtc,
                            StatusText: "Shell closed",
                            Summary: summary,
                            OutputFrames: outputFrames.ToArray())
                        {
                            CanCancel = false,
                            CanSendInput = false
                        },
                        OutputFrame: null,
                        ErrorMessage: null));
            }
        }

        public void PublishCancelled(DateTimeOffset occurredAtUtc, string summary)
        {
            lock (gate)
            {
                if (TerminalAtUtc.HasValue || Runtime is null)
                {
                    return;
                }

                TerminalAtUtc = occurredAtUtc;
                AddMessage(
                    new KubePodExecStreamMessage(
                        MessageType: KubePodExecStreamMessageType.Cancelled,
                        OccurredAtUtc: occurredAtUtc,
                        SessionId: SessionId,
                        Snapshot: new KubePodExecSessionSnapshot(
                            SessionId,
                            Runtime.Resource,
                            Runtime.ContainerName,
                            Runtime.Command,
                            occurredAtUtc,
                            StatusText: "Shell closed",
                            Summary: summary,
                            OutputFrames: outputFrames.ToArray())
                        {
                            CanCancel = false,
                            CanSendInput = false
                        },
                        OutputFrame: null,
                        ErrorMessage: null));
            }
        }

        public void PublishError(string errorMessage, string errorGuidance, DateTimeOffset occurredAtUtc)
        {
            lock (gate)
            {
                if (TerminalAtUtc.HasValue)
                {
                    return;
                }

                TerminalAtUtc = occurredAtUtc;
                AddMessage(
                    new KubePodExecStreamMessage(
                        MessageType: KubePodExecStreamMessageType.Error,
                        OccurredAtUtc: occurredAtUtc,
                        SessionId: SessionId,
                        Snapshot: Runtime is null
                            ? null
                            : new KubePodExecSessionSnapshot(
                                SessionId,
                                Runtime.Resource,
                                Runtime.ContainerName,
                                Runtime.Command,
                                occurredAtUtc,
                                StatusText: "Shell failed",
                                Summary: "The local agent could not keep the exec session open.",
                                OutputFrames: outputFrames.ToArray())
                            {
                                CanCancel = false,
                                CanSendInput = false
                            },
                        OutputFrame: null,
                        ErrorMessage: errorMessage,
                        ErrorGuidance: errorGuidance));
            }
        }

        public async Task<KubePodExecInputResult> SendInputAsync(
            KubePodExecInputRequest request,
            DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken)
        {
            if (Runtime is null)
            {
                return new KubePodExecInputResult(false, HttpConflict, "The exec shell is not ready for input.");
            }

            if (TerminalAtUtc.HasValue)
            {
                return new KubePodExecInputResult(false, HttpConflict, "The exec shell already reached a terminal state.");
            }

            var payload = request.AppendNewline ? $"{request.Text}{Environment.NewLine}" : request.Text;

            try
            {
                await Runtime.SendInputAsync(payload, cancellationToken);
                PublishSnapshot(
                    "Shell ready",
                    "The local agent sent input to the remote shell.",
                    occurredAtUtc,
                    canCancel: true,
                    canSendInput: true);
                return new KubePodExecInputResult(true, HttpNoContent, null);
            }
            catch (OperationCanceledException) when (ExecutionCancellation.IsCancellationRequested)
            {
                return new KubePodExecInputResult(false, HttpConflict, "The exec shell is closing and can no longer accept input.");
            }
            catch (Exception exception)
            {
                PublishError(exception.Message, CreateExecErrorGuidance(exception), occurredAtUtc);
                return new KubePodExecInputResult(false, HttpConflict, "The exec shell could not accept input.");
            }
        }

        public KubePodExecCancelResult RequestCancellation(DateTimeOffset requestedAtUtc)
        {
            lock (gate)
            {
                if (TerminalAtUtc.HasValue)
                {
                    return new KubePodExecCancelResult(false, HttpConflict, "This exec shell already reached a terminal state.");
                }

                if (ExecutionCancellation.IsCancellationRequested)
                {
                    return new KubePodExecCancelResult(false, HttpConflict, "The browser already requested that this exec shell close.");
                }

                if (Runtime is not null)
                {
                    AddMessage(
                        new KubePodExecStreamMessage(
                            MessageType: KubePodExecStreamMessageType.Snapshot,
                            OccurredAtUtc: requestedAtUtc,
                            SessionId: SessionId,
                            Snapshot: new KubePodExecSessionSnapshot(
                                SessionId,
                                Runtime.Resource,
                                Runtime.ContainerName,
                                Runtime.Command,
                                requestedAtUtc,
                                StatusText: "Closing shell",
                                Summary: "The browser requested that the local agent close the remote shell.",
                                OutputFrames: outputFrames.ToArray())
                            {
                                CanCancel = false,
                                CanSendInput = false
                            },
                            OutputFrame: null,
                            ErrorMessage: null));
                }

                ExecutionCancellation.Cancel();
                return new KubePodExecCancelResult(true, HttpNoContent, null);
            }
        }

        public PodExecReadBatch ReadFrom(int sentCount)
        {
            lock (gate)
            {
                if (sentCount < messages.Count)
                {
                    return new PodExecReadBatch(
                        Messages: messages.Skip(sentCount).ToArray(),
                        WaitForUpdate: Task.CompletedTask,
                        IsTerminal: TerminalAtUtc.HasValue);
                }

                return new PodExecReadBatch(
                    Messages: [],
                    WaitForUpdate: nextUpdate.Task,
                    IsTerminal: TerminalAtUtc.HasValue);
            }
        }

        public async Task DisposeRuntimeAsync()
        {
            var runtime = Runtime;
            Runtime = null;

            if (runtime is not null)
            {
                await runtime.DisposeAsync();
            }
        }

        private void AddMessage(KubePodExecStreamMessage message)
        {
            messages.Add(message);
            var signal = nextUpdate;
            nextUpdate = CreateSignal();
            signal.TrySetResult(true);
        }

        private static TaskCompletionSource<bool> CreateSignal()
        {
            return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private sealed record PodExecReadBatch(
        IReadOnlyList<KubePodExecStreamMessage> Messages,
        Task WaitForUpdate,
        bool IsTerminal);
}

public sealed record KubePodExecCancelResult(
    bool Success,
    int StatusCode,
    string? ErrorMessage);

public sealed record KubePodExecInputResult(
    bool Success,
    int StatusCode,
    string? ErrorMessage);
