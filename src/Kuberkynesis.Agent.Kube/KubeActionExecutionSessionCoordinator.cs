using Kuberkynesis.Agent.Core.Security;
using k8s.Autorest;
using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Kube;

public sealed class KubeActionExecutionSessionCoordinator
{
    private static readonly TimeSpan CompletedSessionRetention = TimeSpan.FromMinutes(10);
    private const int HttpOk = 200;
    private const int HttpNoContent = 204;
    private const int HttpForbidden = 403;
    private const int HttpNotFound = 404;
    private const int HttpConflict = 409;

    private readonly object gate = new();
    private readonly IKubeActionExecutionService executionService;
    private readonly TimeProvider timeProvider;
    private readonly Dictionary<string, ActionExecutionSession> sessions = new(StringComparer.Ordinal);

    public KubeActionExecutionSessionCoordinator(
        IKubeActionExecutionService executionService,
        TimeProvider? timeProvider = null)
    {
        this.executionService = executionService;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public KubeActionExecutionStartResponse StartExecution(
        AuthenticatedAgentSession session,
        KubeActionExecuteRequest request)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);

        CleanupExpiredSessions();

        var executionId = $"axe_{Guid.NewGuid():N}";
        var startedAtUtc = GetUtcNow();
        var resource = CreateResourceIdentity(request);
        var initialSnapshot = new KubeActionExecutionProgressSnapshot(
            ExecutionId: executionId,
            Action: request.Action,
            Resource: resource,
            UpdatedAtUtc: startedAtUtc,
            StatusText: "Starting",
            Summary: "Rechecking the guarded preview before submitting the mutation.");

        var executionSession = new ActionExecutionSession(
            executionId,
            session.SessionToken,
            request,
            initialSnapshot);

        lock (gate)
        {
            sessions[executionId] = executionSession;
        }

        _ = Task.Run(() => RunExecutionAsync(executionSession), CancellationToken.None);

        return new KubeActionExecutionStartResponse(
            ExecutionId: executionId,
            Action: request.Action,
            Resource: resource,
            StartedAtUtc: startedAtUtc,
            StatusText: initialSnapshot.StatusText,
            Summary: initialSnapshot.Summary)
        {
            CanCancel = true
        };
    }

    public KubeActionExecutionCancelResult CancelExecution(
        AuthenticatedAgentSession session,
        string executionId)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

        if (!TryGetAuthorizedSession(executionId, session, out var executionSession, out var statusCode, out var errorMessage))
        {
            return new KubeActionExecutionCancelResult(false, statusCode, errorMessage);
        }

        return executionSession!.RequestCancellation(GetUtcNow());
    }

    public bool TryAuthorizeExecution(
        string executionId,
        AuthenticatedAgentSession session,
        out int statusCode,
        out string? errorMessage)
    {
        return TryGetAuthorizedSession(executionId, session, out _, out statusCode, out errorMessage);
    }

    public async Task StreamAsync(
        string executionId,
        AuthenticatedAgentSession session,
        Func<KubeActionExecutionStreamMessage, CancellationToken, Task> onMessage,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(onMessage);

        if (!TryGetAuthorizedSession(executionId, session, out var executionSession, out var statusCode, out var errorMessage))
        {
            throw CreateStreamAuthorizationException(statusCode, errorMessage);
        }

        var sentCount = 0;

        while (true)
        {
            var batch = executionSession!.ReadFrom(sentCount);

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

    private async Task RunExecutionAsync(ActionExecutionSession executionSession)
    {
        try
        {
            var result = await executionService.ExecuteAsync(
                executionSession.Request,
                progress => executionSession.PublishSnapshot(MapProgressSnapshot(executionSession, progress)),
                executionSession.ExecutionCancellation.Token);

            executionSession.PublishCompleted(result);
        }
        catch (OperationCanceledException) when (executionSession.ExecutionCancellation.IsCancellationRequested)
        {
            executionSession.PublishCancelled(BuildCancelledResponse(executionSession));
        }
        catch (Exception exception)
        {
            executionSession.PublishError(
                exception.Message,
                CreateExecutionErrorGuidance(exception),
                GetUtcNow());
        }
    }

    private bool TryGetAuthorizedSession(
        string executionId,
        AuthenticatedAgentSession session,
        out ActionExecutionSession? executionSession,
        out int statusCode,
        out string? errorMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        ArgumentNullException.ThrowIfNull(session);

        CleanupExpiredSessions();

        lock (gate)
        {
            if (!sessions.TryGetValue(executionId, out executionSession))
            {
                statusCode = HttpNotFound;
                errorMessage = "The requested action execution is no longer available.";
                return false;
            }

            if (!string.Equals(executionSession.OwnerSessionToken, session.SessionToken, StringComparison.Ordinal))
            {
                executionSession = null;
                statusCode = HttpForbidden;
                errorMessage = "This action execution belongs to a different browser session.";
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
            foreach (var executionId in sessions
                         .Where(static pair => pair.Value.CanExpire)
                         .Where(pair => pair.Value.TerminalAtUtc <= cutoffUtc)
                         .Select(static pair => pair.Key)
                         .ToArray())
            {
                sessions.Remove(executionId);
            }
        }
    }

    private DateTimeOffset GetUtcNow() => timeProvider.GetUtcNow();

    private static KubeResourceIdentity CreateResourceIdentity(KubeActionExecuteRequest request)
    {
        return new KubeResourceIdentity(
            ContextName: request.ContextName.Trim(),
            Kind: request.Kind,
            Namespace: string.IsNullOrWhiteSpace(request.Namespace) ? null : request.Namespace.Trim(),
            Name: request.Name.Trim());
    }

    private static KubeActionExecutionProgressSnapshot MapProgressSnapshot(
        ActionExecutionSession executionSession,
        KubeActionExecutionProgressUpdate progress)
    {
        return new KubeActionExecutionProgressSnapshot(
            ExecutionId: executionSession.ExecutionId,
            Action: executionSession.Request.Action,
            Resource: CreateResourceIdentity(executionSession.Request),
            UpdatedAtUtc: DateTimeOffset.UtcNow,
            StatusText: progress.StatusText,
            Summary: progress.Summary)
        {
            CanCancel = progress.CanCancel
        };
    }

    private static KubeActionExecuteResponse BuildCancelledResponse(ActionExecutionSession executionSession)
    {
        var resource = CreateResourceIdentity(executionSession.Request);
        var completedAtUtc = DateTimeOffset.UtcNow;

        return new KubeActionExecuteResponse(
            Action: executionSession.Request.Action,
            Resource: resource,
            Summary: $"The {GetResourceLabel(resource)} request was cancelled before the local agent reported a completed mutation result.",
            CompletedAtUtc: completedAtUtc,
            Facts:
            [
                new KubeActionPreviewFact("Final status", "Cancelled")
            ],
            Notes:
            [
                "The browser requested cancellation before the agent reported a terminal mutation result.",
                "Refresh the current surface to confirm whether the cluster applied any partial change before cancellation took effect."
            ],
            TransparencyCommands: [])
        {
            Status = KubeActionExecutionStatus.Cancelled,
            RequestedTargetCount = 1,
            AttemptedTargetCount = 0,
            SucceededTargetCount = 0,
            FailedTargetCount = 0,
            SkippedTargetCount = 1,
            TargetResults =
            [
                new KubeActionExecutionTargetResult(
                    resource,
                    KubeActionExecutionStatus.Cancelled,
                    "The browser requested cancellation before the local agent reported completion.")
            ]
        };
    }

    private static Exception CreateStreamAuthorizationException(int statusCode, string? errorMessage)
    {
        return statusCode switch
        {
            HttpNotFound => new KeyNotFoundException(errorMessage),
            _ => new UnauthorizedAccessException(errorMessage)
        };
    }

    private static string CreateExecutionErrorGuidance(Exception exception)
    {
        return exception switch
        {
            HttpOperationException httpException when httpException.Response.StatusCode is System.Net.HttpStatusCode.Forbidden =>
                "Switch to a kubeconfig or context with the required update permission, then preview and retry the mutation.",
            HttpOperationException httpException when httpException.Response.StatusCode is System.Net.HttpStatusCode.NotFound =>
                "Refresh the current surface and confirm the target resource still exists before retrying the mutation.",
            InvalidOperationException =>
                "Refresh the preview and review the current guardrails before retrying the mutation.",
            ArgumentException =>
                "Adjust the current request or preview inputs, then retry the mutation.",
            _ =>
                "Refresh the current surface to confirm cluster state before retrying the mutation."
        };
    }

    private static string GetResourceLabel(KubeResourceIdentity resource)
    {
        return resource.Kind is null
            ? resource.Name
            : $"{resource.Kind}/{resource.Name}";
    }

    private sealed class ActionExecutionSession
    {
        private readonly object gate = new();
        private readonly List<KubeActionExecutionStreamMessage> messages = [];
        private TaskCompletionSource<bool> nextUpdate = CreateSignal();

        public ActionExecutionSession(
            string executionId,
            string ownerSessionToken,
            KubeActionExecuteRequest request,
            KubeActionExecutionProgressSnapshot initialSnapshot)
        {
            ExecutionId = executionId;
            OwnerSessionToken = ownerSessionToken;
            Request = request;
            ExecutionCancellation = new CancellationTokenSource();

            AddMessage(
                new KubeActionExecutionStreamMessage(
                    MessageType: KubeActionExecutionStreamMessageType.Snapshot,
                    OccurredAtUtc: initialSnapshot.UpdatedAtUtc,
                    ExecutionId: executionId,
                    Snapshot: initialSnapshot,
                    Result: null,
                    ErrorMessage: null));
        }

        public string ExecutionId { get; }

        public string OwnerSessionToken { get; }

        public KubeActionExecuteRequest Request { get; }

        public CancellationTokenSource ExecutionCancellation { get; }

        public DateTimeOffset? TerminalAtUtc { get; private set; }

        public bool CanExpire => TerminalAtUtc.HasValue;

        public void PublishSnapshot(KubeActionExecutionProgressSnapshot snapshot)
        {
            lock (gate)
            {
                if (TerminalAtUtc.HasValue)
                {
                    return;
                }

                AddMessage(
                    new KubeActionExecutionStreamMessage(
                        MessageType: KubeActionExecutionStreamMessageType.Snapshot,
                        OccurredAtUtc: snapshot.UpdatedAtUtc,
                        ExecutionId: ExecutionId,
                        Snapshot: snapshot,
                        Result: null,
                        ErrorMessage: null));
            }
        }

        public void PublishCompleted(KubeActionExecuteResponse result)
        {
            lock (gate)
            {
                if (TerminalAtUtc.HasValue)
                {
                    return;
                }

                TerminalAtUtc = result.CompletedAtUtc;
                AddMessage(
                    new KubeActionExecutionStreamMessage(
                        MessageType: KubeActionExecutionStreamMessageType.Completed,
                        OccurredAtUtc: result.CompletedAtUtc,
                        ExecutionId: ExecutionId,
                        Snapshot: null,
                        Result: result,
                        ErrorMessage: null));
            }
        }

        public void PublishCancelled(KubeActionExecuteResponse result)
        {
            lock (gate)
            {
                if (TerminalAtUtc.HasValue)
                {
                    return;
                }

                TerminalAtUtc = result.CompletedAtUtc;
                AddMessage(
                    new KubeActionExecutionStreamMessage(
                        MessageType: KubeActionExecutionStreamMessageType.Cancelled,
                        OccurredAtUtc: result.CompletedAtUtc,
                        ExecutionId: ExecutionId,
                        Snapshot: null,
                        Result: result,
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
                    new KubeActionExecutionStreamMessage(
                        MessageType: KubeActionExecutionStreamMessageType.Error,
                        OccurredAtUtc: occurredAtUtc,
                        ExecutionId: ExecutionId,
                        Snapshot: null,
                        Result: null,
                        ErrorMessage: errorMessage,
                        ErrorGuidance: errorGuidance));
            }
        }

        public KubeActionExecutionCancelResult RequestCancellation(DateTimeOffset requestedAtUtc)
        {
            lock (gate)
            {
                if (TerminalAtUtc.HasValue)
                {
                    return new KubeActionExecutionCancelResult(
                        false,
                        HttpConflict,
                        "This action execution already reached a terminal state.");
                }

                if (ExecutionCancellation.IsCancellationRequested)
                {
                    return new KubeActionExecutionCancelResult(
                        false,
                        HttpConflict,
                        "Cancellation was already requested for this action execution.");
                }

                AddMessage(
                    new KubeActionExecutionStreamMessage(
                        MessageType: KubeActionExecutionStreamMessageType.Snapshot,
                        OccurredAtUtc: requestedAtUtc,
                        ExecutionId: ExecutionId,
                        Snapshot: new KubeActionExecutionProgressSnapshot(
                            ExecutionId: ExecutionId,
                            Action: Request.Action,
                            Resource: CreateResourceIdentity(Request),
                            UpdatedAtUtc: requestedAtUtc,
                            StatusText: "Cancelling",
                            Summary: "Cancellation was requested. Waiting for the current Kubernetes call to stop.")
                        {
                            CanCancel = false
                        },
                        Result: null,
                        ErrorMessage: null));

                ExecutionCancellation.Cancel();

                return new KubeActionExecutionCancelResult(true, HttpNoContent, null);
            }
        }

        public ActionExecutionReadBatch ReadFrom(int sentCount)
        {
            lock (gate)
            {
                if (sentCount < messages.Count)
                {
                    return new ActionExecutionReadBatch(
                        Messages: messages.Skip(sentCount).ToArray(),
                        WaitForUpdate: Task.CompletedTask,
                        IsTerminal: TerminalAtUtc.HasValue);
                }

                return new ActionExecutionReadBatch(
                    Messages: [],
                    WaitForUpdate: nextUpdate.Task,
                    IsTerminal: TerminalAtUtc.HasValue);
            }
        }

        private void AddMessage(KubeActionExecutionStreamMessage message)
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

    private sealed record ActionExecutionReadBatch(
        IReadOnlyList<KubeActionExecutionStreamMessage> Messages,
        Task WaitForUpdate,
        bool IsTerminal);
}

public sealed record KubeActionExecutionCancelResult(
    bool Success,
    int StatusCode,
    string? ErrorMessage);
