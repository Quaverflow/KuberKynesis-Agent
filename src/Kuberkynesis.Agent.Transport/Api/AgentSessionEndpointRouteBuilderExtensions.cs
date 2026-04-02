using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using k8s.Autorest;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc;
using Kuberkynesis.Agent.Core.Configuration;
using Kuberkynesis.Agent.Core.Security;
using Kuberkynesis.Agent.Kube;
using Kuberkynesis.Ui.Shared.Connection;
using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Transport.Api;

public static class AgentSessionEndpointRouteBuilderExtensions
{
    private static readonly JsonSerializerOptions WebSocketSerializerOptions = CreateWebSocketSerializerOptions();

    public static IEndpointRouteBuilder MapAgentSessionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/v1");

        group.MapGet("/hello", (PairingSessionRegistry sessions, OriginAccessClassifier classifier) =>
            Results.Ok(sessions.CreateHelloResponse(classifier)));

        group.MapPost("/pair", (HttpContext httpContext, PairRequest request, PairingSessionRegistry sessions, OriginAccessClassifier classifier) =>
        {
            var headerOrigin = httpContext.Request.Headers.Origin.ToString();

            if (!string.IsNullOrWhiteSpace(headerOrigin) &&
                !string.Equals(headerOrigin, request.Origin, StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new
                {
                    error = "Origin header and request payload origin do not match."
                });
            }

            var effectiveOrigin = !string.IsNullOrWhiteSpace(headerOrigin) ? headerOrigin : request.Origin;
            var decision = classifier.Evaluate(effectiveOrigin);
            var result = sessions.TryPair(request, effectiveOrigin, decision);

            return result.Success
                ? Results.Ok(result.Response)
                : Results.Json(
                    new { error = result.ErrorMessage },
                    statusCode: result.StatusCode);
        });

        group.MapGet("/session", (HttpContext httpContext, PairingSessionRegistry sessions) =>
        {
            var authorizationFailure = TryRequireAuthenticatedHttpSession(httpContext, out var session);

            return authorizationFailure ?? Results.Ok(sessions.CreateSessionResponse(session!));
        });

        group.MapDelete("/session", (HttpContext httpContext, PairingSessionRegistry sessions) =>
        {
            var authorizationFailure = TryRequireAuthenticatedHttpSession(httpContext, out var session);

            if (authorizationFailure is not null)
            {
                return authorizationFailure;
            }

            sessions.RevokeSession(session!);
            return Results.NoContent();
        });

        group.MapPost("/session/release", async (HttpContext httpContext, PairingSessionRegistry sessions, CancellationToken cancellationToken) =>
        {
            using var reader = new StreamReader(httpContext.Request.Body, Encoding.UTF8, leaveOpen: false);
            var sessionToken = (await reader.ReadToEndAsync(cancellationToken)).Trim();
            var origin = httpContext.Request.Headers.Origin.ToString();

            sessions.ScheduleSessionRelease(sessionToken, origin);
            return Results.NoContent();
        });

        group.MapPost("/session/ws-ticket", (HttpContext httpContext, PairingSessionRegistry sessions) =>
        {
            var authorizationFailure = TryRequireAuthenticatedHttpSession(httpContext, out var session);

            return authorizationFailure ?? Results.Ok(sessions.CreateWebSocketTicket(session!));
        });

        group.MapGet("/diagnostics", async (
            HttpContext httpContext,
            PairingSessionRegistry sessions,
            AgentRuntimeOptions runtimeOptions,
            KubeBootstrapProbe kubeBootstrapProbe,
            KubeContextDiscoveryService discovery,
            AgentDiagnosticsResponseFactory diagnosticsFactory,
            CancellationToken cancellationToken) =>
        {
            var authorizationFailure = TryRequireAuthenticatedHttpSession(httpContext, out _);

            if (authorizationFailure is not null)
            {
                return authorizationFailure;
            }

            var probe = kubeBootstrapProbe.Probe();
            var contexts = await discovery.GetContextsAsync(cancellationToken);

            return Results.Ok(diagnosticsFactory.Create(runtimeOptions, sessions, probe, contexts.Contexts));
        });

        group.MapGet("/contexts", async (HttpContext httpContext, PairingSessionRegistry sessions, KubeContextDiscoveryService discovery, CancellationToken cancellationToken) =>
        {
            var authorizationFailure = TryRequireAuthenticatedHttpSession(httpContext, out _);

            return authorizationFailure ?? Results.Ok(await discovery.GetContextsAsync(cancellationToken));
        });

        group.MapPost("/resources/custom-definitions", async (
            HttpContext httpContext,
            [FromBody] IReadOnlyList<string> contexts,
            PairingSessionRegistry sessions,
            KubeCustomResourceDefinitionService definitionService,
            CancellationToken cancellationToken) =>
        {
            var authorizationFailure = TryRequireAuthenticatedHttpSession(httpContext, out _);

            if (authorizationFailure is not null)
            {
                return authorizationFailure;
            }

            try
            {
                var response = await definitionService.GetDefinitionsAsync(contexts, cancellationToken);
                return Results.Ok(response);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new
                {
                    error = exception.Message
                });
            }
        });

        group.MapPost("/workspace/resolve", async (
            HttpContext httpContext,
            KubeWorkspaceResolveRequest request,
            PairingSessionRegistry sessions,
            KubeWorkspaceResolveService resolveService,
            CancellationToken cancellationToken) =>
        {
            var authorizationFailure = TryRequireAuthenticatedHttpSession(httpContext, out _);

            if (authorizationFailure is not null)
            {
                return authorizationFailure;
            }

            try
            {
                var response = await resolveService.ResolveAsync(request, cancellationToken);
                return Results.Ok(response);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new
                {
                    error = exception.Message
                });
            }
        });

        group.MapPost("/resources/query", async (
            HttpContext httpContext,
            KubeResourceQueryRequest request,
            PairingSessionRegistry sessions,
            KubeResourceQueryService queryService,
            CancellationToken cancellationToken) =>
        {
            var authorizationFailure = TryRequireAuthenticatedHttpSession(httpContext, out _);

            if (authorizationFailure is not null)
            {
                return authorizationFailure;
            }

            try
            {
                var response = await queryService.QueryAsync(request, cancellationToken);
                return Results.Ok(response);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new
                {
                    error = exception.Message
                });
            }
        });

        group.MapPost("/resources/detail", async (
            HttpContext httpContext,
            KubeResourceDetailRequest request,
            PairingSessionRegistry sessions,
            KubeResourceDetailService detailService,
            CancellationToken cancellationToken) =>
        {
            var authorizationFailure = TryRequireAuthenticatedHttpSession(httpContext, out _);

            if (authorizationFailure is not null)
            {
                return authorizationFailure;
            }

            try
            {
                var response = await detailService.GetDetailAsync(request, cancellationToken);
                return Results.Ok(response);
            }
            catch (HttpOperationException exception) when (exception.Response.StatusCode is HttpStatusCode.NotFound)
            {
                return Results.NotFound(new
                {
                    error = $"The {request.Kind} '{request.Name}' was not found."
                });
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new
                {
                    error = exception.Message
                });
            }
        });

        group.MapPost("/actions/preview", async (
            HttpContext httpContext,
            [FromBody] KubeActionPreviewRequest request,
            PairingSessionRegistry sessions,
            [FromServices] KubeActionPreviewService previewService,
            CancellationToken cancellationToken) =>
        {
            var authorizationFailure = TryRequireAuthenticatedHttpSession(httpContext, out _);

            if (authorizationFailure is not null)
            {
                return authorizationFailure;
            }

            try
            {
                var response = await previewService.GetPreviewAsync(request, cancellationToken);
                return Results.Ok(response);
            }
            catch (HttpOperationException exception) when (exception.Response.StatusCode is HttpStatusCode.NotFound)
            {
                return Results.NotFound(new
                {
                    error = $"The {request.Kind} '{request.Name}' was not found."
                });
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new
                {
                    error = exception.Message
                });
            }
        });

        group.MapPost("/actions/execute/start", (
            HttpContext httpContext,
            [FromBody] KubeActionExecuteRequest request,
            PairingSessionRegistry sessions,
            [FromServices] KubeActionExecutionSessionCoordinator executionCoordinator) =>
        {
            var authorizationFailure = TryRequireInteractiveMutationSession(httpContext, out var session);

            if (authorizationFailure is not null)
            {
                return authorizationFailure;
            }

            try
            {
                var response = executionCoordinator.StartExecution(session!, request);
                return Results.Ok(response);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new
                {
                    error = exception.Message
                });
            }
        });

        group.MapDelete("/actions/execute/{executionId}", (
            HttpContext httpContext,
            string executionId,
            PairingSessionRegistry sessions,
            [FromServices] KubeActionExecutionSessionCoordinator executionCoordinator) =>
        {
            var authorizationFailure = TryRequireInteractiveMutationSession(httpContext, out var session);

            if (authorizationFailure is not null)
            {
                return authorizationFailure;
            }

            var cancelResult = executionCoordinator.CancelExecution(session!, executionId);

            return cancelResult.Success
                ? Results.NoContent()
                : Results.Json(
                    new
                    {
                        error = cancelResult.ErrorMessage
                    },
                    statusCode: cancelResult.StatusCode);
        });

        group.MapPost("/actions/execute", async (
            HttpContext httpContext,
            [FromBody] KubeActionExecuteRequest request,
            PairingSessionRegistry sessions,
            [FromServices] IKubeActionExecutionService executionService,
            CancellationToken cancellationToken) =>
        {
            var authorizationFailure = TryRequireInteractiveMutationSession(httpContext, out _);

            if (authorizationFailure is not null)
            {
                return authorizationFailure;
            }

            try
            {
                var response = await executionService.ExecuteAsync(request, cancellationToken);
                return Results.Ok(response);
            }
            catch (HttpOperationException exception) when (exception.Response.StatusCode is HttpStatusCode.Forbidden)
            {
                return Results.Json(new
                {
                    error = BuildKubernetesForbiddenMutationMessage(request)
                }, statusCode: StatusCodes.Status403Forbidden);
            }
            catch (HttpOperationException exception) when (exception.Response.StatusCode is HttpStatusCode.NotFound)
            {
                return Results.NotFound(new
                {
                    error = $"The {request.Kind} '{request.Name}' was not found."
                });
            }
            catch (InvalidOperationException exception)
            {
                return Results.Json(new
                {
                    error = exception.Message
                }, statusCode: StatusCodes.Status409Conflict);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new
                {
                    error = exception.Message
                });
            }
        });

        group.MapGet("/actions/stream", async (
            HttpContext httpContext,
            PairingSessionRegistry sessions,
            [FromServices] KubeActionExecutionSessionCoordinator executionCoordinator,
            CancellationToken cancellationToken) =>
        {
            if (!httpContext.WebSockets.IsWebSocketRequest)
            {
                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                await httpContext.Response.WriteAsJsonAsync(new
                {
                    error = "The action execution stream endpoint requires a WebSocket request."
                }, cancellationToken);

                return;
            }

            if (!TryParseActionExecutionStreamRequest(httpContext.Request, out var wsTicket, out var executionId, out var errorMessage))
            {
                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                await httpContext.Response.WriteAsJsonAsync(new
                {
                    error = errorMessage
                }, cancellationToken);

                return;
            }

            var origin = httpContext.Request.Headers.Origin.ToString();
            var authorization = sessions.AuthorizeWebSocketTicket(wsTicket, origin);

            if (!authorization.Success)
            {
                httpContext.Response.StatusCode = authorization.StatusCode;
                await httpContext.Response.WriteAsJsonAsync(new
                {
                    error = authorization.ErrorMessage
                }, cancellationToken);

                return;
            }

            if (authorization.Session!.GrantedMode is not OriginAccessClass.Interactive)
            {
                httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
                await httpContext.Response.WriteAsJsonAsync(new
                {
                    error = "This session is read-only preview only and cannot subscribe to mutation execution streams."
                }, cancellationToken);

                return;
            }

            if (!executionCoordinator.TryAuthorizeExecution(executionId!, authorization.Session, out var statusCode, out errorMessage))
            {
                httpContext.Response.StatusCode = statusCode;
                await httpContext.Response.WriteAsJsonAsync(new
                {
                    error = errorMessage
                }, cancellationToken);

                return;
            }

            using var webSocket = await httpContext.WebSockets.AcceptWebSocketAsync();

            try
            {
                await executionCoordinator.StreamAsync(
                    executionId!,
                    authorization.Session,
                    (message, token) => SendWebSocketJsonAsync(webSocket, message, token),
                    cancellationToken);
            }
            catch (Exception exception)
            {
                if (exception is OperationCanceledException)
                {
                    return;
                }

                await SendWebSocketJsonAsync(
                    webSocket,
                    new KubeActionExecutionStreamMessage(
                        MessageType: KubeActionExecutionStreamMessageType.Error,
                        OccurredAtUtc: DateTimeOffset.UtcNow,
                        ExecutionId: executionId!,
                        Snapshot: null,
                        Result: null,
                        ErrorMessage: exception.Message,
                        ErrorGuidance: null),
                    cancellationToken);
            }
            finally
            {
                if (webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await webSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Action execution stream closed.",
                        CancellationToken.None);
                }
            }
        });

        group.MapPost("/exec/start", async (
            HttpContext httpContext,
            [FromBody] KubePodExecStartRequest request,
            PairingSessionRegistry sessions,
            [FromServices] KubePodExecSessionCoordinator execCoordinator,
            CancellationToken cancellationToken) =>
        {
            var authorizationFailure = TryRequireInteractiveExecSession(httpContext, out var session);

            if (authorizationFailure is not null)
            {
                return authorizationFailure;
            }

            try
            {
                var response = await execCoordinator.StartSessionAsync(session!, request, cancellationToken);
                return Results.Ok(response);
            }
            catch (HttpOperationException exception) when (exception.Response.StatusCode is HttpStatusCode.Forbidden)
            {
                return Results.Json(
                    new
                    {
                        error = BuildKubernetesForbiddenExecMessage(request)
                    },
                    statusCode: StatusCodes.Status403Forbidden);
            }
            catch (HttpOperationException exception) when (exception.Response.StatusCode is HttpStatusCode.NotFound)
            {
                return Results.NotFound(new
                {
                    error = $"The pod '{request.PodName}' was not found."
                });
            }
            catch (InvalidOperationException exception)
            {
                return Results.Json(new
                {
                    error = exception.Message
                }, statusCode: StatusCodes.Status409Conflict);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new
                {
                    error = exception.Message
                });
            }
        });

        group.MapPost("/exec/{sessionId}/input", async (
            HttpContext httpContext,
            string sessionId,
            [FromBody] KubePodExecInputRequest request,
            PairingSessionRegistry sessions,
            [FromServices] KubePodExecSessionCoordinator execCoordinator,
            CancellationToken cancellationToken) =>
        {
            var authorizationFailure = TryRequireInteractiveExecSession(httpContext, out var session);

            if (authorizationFailure is not null)
            {
                return authorizationFailure;
            }

            var inputResult = await execCoordinator.SendInputAsync(session!, sessionId, request, cancellationToken);

            return inputResult.Success
                ? Results.NoContent()
                : Results.Json(
                    new
                    {
                        error = inputResult.ErrorMessage
                    },
                    statusCode: inputResult.StatusCode);
        });

        group.MapDelete("/exec/{sessionId}", (
            HttpContext httpContext,
            string sessionId,
            PairingSessionRegistry sessions,
            [FromServices] KubePodExecSessionCoordinator execCoordinator) =>
        {
            var authorizationFailure = TryRequireInteractiveExecSession(httpContext, out var session);

            if (authorizationFailure is not null)
            {
                return authorizationFailure;
            }

            var cancelResult = execCoordinator.CancelSession(session!, sessionId);

            return cancelResult.Success
                ? Results.NoContent()
                : Results.Json(
                    new
                    {
                        error = cancelResult.ErrorMessage
                    },
                    statusCode: cancelResult.StatusCode);
        });

        group.MapGet("/exec/stream", async (
            HttpContext httpContext,
            PairingSessionRegistry sessions,
            [FromServices] KubePodExecSessionCoordinator execCoordinator,
            CancellationToken cancellationToken) =>
        {
            if (!httpContext.WebSockets.IsWebSocketRequest)
            {
                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                await httpContext.Response.WriteAsJsonAsync(new
                {
                    error = "The pod exec stream endpoint requires a WebSocket request."
                }, cancellationToken);

                return;
            }

            if (!TryParsePodExecStreamRequest(httpContext.Request, out var wsTicket, out var sessionId, out var errorMessage))
            {
                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                await httpContext.Response.WriteAsJsonAsync(new
                {
                    error = errorMessage
                }, cancellationToken);

                return;
            }

            var origin = httpContext.Request.Headers.Origin.ToString();
            var authorization = sessions.AuthorizeWebSocketTicket(wsTicket, origin);

            if (!authorization.Success)
            {
                httpContext.Response.StatusCode = authorization.StatusCode;
                await httpContext.Response.WriteAsJsonAsync(new
                {
                    error = authorization.ErrorMessage
                }, cancellationToken);

                return;
            }

            if (authorization.Session!.GrantedMode is not OriginAccessClass.Interactive)
            {
                httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
                await httpContext.Response.WriteAsJsonAsync(new
                {
                    error = "This session is read-only preview only and cannot attach to interactive exec shells."
                }, cancellationToken);

                return;
            }

            if (!execCoordinator.TryAuthorizeSession(sessionId!, authorization.Session, out var statusCode, out errorMessage))
            {
                httpContext.Response.StatusCode = statusCode;
                await httpContext.Response.WriteAsJsonAsync(new
                {
                    error = errorMessage
                }, cancellationToken);

                return;
            }

            using var webSocket = await httpContext.WebSockets.AcceptWebSocketAsync();

            try
            {
                await execCoordinator.StreamAsync(
                    sessionId!,
                    authorization.Session,
                    (message, token) => SendWebSocketJsonAsync(webSocket, message, token),
                    cancellationToken);
            }
            catch (Exception exception)
            {
                if (exception is OperationCanceledException)
                {
                    return;
                }

                await SendWebSocketJsonAsync(
                    webSocket,
                    new KubePodExecStreamMessage(
                        MessageType: KubePodExecStreamMessageType.Error,
                        OccurredAtUtc: DateTimeOffset.UtcNow,
                        SessionId: sessionId!,
                        Snapshot: null,
                        OutputFrame: null,
                        ErrorMessage: exception.Message,
                        ErrorGuidance: null),
                    cancellationToken);
            }
            finally
            {
                if (webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await webSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Pod exec stream completed",
                        cancellationToken);
                }
            }
        });

        group.MapPost("/resources/graph", async (
            HttpContext httpContext,
            KubeResourceGraphRequest request,
            PairingSessionRegistry sessions,
            KubeResourceGraphService graphService,
            CancellationToken cancellationToken) =>
        {
            var authorizationFailure = TryRequireAuthenticatedHttpSession(httpContext, out _);

            if (authorizationFailure is not null)
            {
                return authorizationFailure;
            }

            try
            {
                var response = await graphService.GetGraphAsync(request, cancellationToken);
                return Results.Ok(response);
            }
            catch (HttpOperationException exception) when (exception.Response.StatusCode is HttpStatusCode.NotFound)
            {
                return Results.NotFound(new
                {
                    error = $"The {request.Kind} '{request.Name}' was not found."
                });
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new
                {
                    error = exception.Message
                });
            }
        });

        group.MapPost("/resources/metrics", async (
            HttpContext httpContext,
            [FromBody] KubeResourceMetricsRequest request,
            PairingSessionRegistry sessions,
            [FromServices] KubeResourceMetricsService metricsService,
            CancellationToken cancellationToken) =>
        {
            var authorizationFailure = TryRequireAuthenticatedHttpSession(httpContext, out _);

            if (authorizationFailure is not null)
            {
                return authorizationFailure;
            }

            try
            {
                var response = await metricsService.GetResourceMetricsAsync(request, cancellationToken);
                return Results.Ok(response);
            }
            catch (HttpOperationException exception) when (exception.Response.StatusCode is HttpStatusCode.NotFound)
            {
                return Results.NotFound(new
                {
                    error = $"The {request.Kind} '{request.Name}' was not found."
                });
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new
                {
                    error = exception.Message
                });
            }
        });

        group.MapPost("/pods/metrics/query", async (
            HttpContext httpContext,
            [FromBody] KubePodMetricsQueryRequest request,
            PairingSessionRegistry sessions,
            [FromServices] KubeResourceMetricsService metricsService,
            CancellationToken cancellationToken) =>
        {
            var authorizationFailure = TryRequireAuthenticatedHttpSession(httpContext, out _);

            if (authorizationFailure is not null)
            {
                return authorizationFailure;
            }

            try
            {
                var response = await metricsService.QueryPodMetricsAsync(request, cancellationToken);
                return Results.Ok(response);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new
                {
                    error = exception.Message
                });
            }
        });

        group.MapPost("/resources/timeline", async (
            HttpContext httpContext,
            KubeResourceTimelineRequest request,
            PairingSessionRegistry sessions,
            KubeResourceTimelineService timelineService,
            CancellationToken cancellationToken) =>
        {
            var authorizationFailure = TryRequireAuthenticatedHttpSession(httpContext, out _);

            if (authorizationFailure is not null)
            {
                return authorizationFailure;
            }

            try
            {
                var response = await timelineService.GetTimelineAsync(request, cancellationToken);
                return Results.Ok(response);
            }
            catch (HttpOperationException exception) when (exception.Response.StatusCode is HttpStatusCode.NotFound)
            {
                return Results.NotFound(new
                {
                    error = $"The {request.Kind} '{request.Name}' was not found."
                });
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new
                {
                    error = exception.Message
                });
            }
        });

        group.MapPost("/live/query", async (
            HttpContext httpContext,
            [FromBody] KubeLiveSurfaceQueryRequest request,
            PairingSessionRegistry sessions,
            [FromServices] KubeLiveSurfaceService liveSurfaceService,
            CancellationToken cancellationToken) =>
        {
            var authorizationFailure = TryRequireAuthenticatedHttpSession(httpContext, out _);

            if (authorizationFailure is not null)
            {
                return authorizationFailure;
            }

            try
            {
                var response = await liveSurfaceService.QueryAsync(request, cancellationToken);
                return Results.Ok(response);
            }
            catch (HttpOperationException exception) when (exception.Response.StatusCode is HttpStatusCode.NotFound)
            {
                return Results.NotFound(new
                {
                    error = $"The {request.Kind} '{request.Name}' was not found."
                });
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new
                {
                    error = exception.Message
                });
            }
        });

        group.MapPost("/pods/logs", async (
            HttpContext httpContext,
            KubePodLogRequest request,
            PairingSessionRegistry sessions,
            KubePodLogService podLogService,
            CancellationToken cancellationToken) =>
        {
            var authorizationFailure = TryRequireAuthenticatedHttpSession(httpContext, out _);

            if (authorizationFailure is not null)
            {
                return authorizationFailure;
            }

            try
            {
                var response = await podLogService.GetLogsAsync(request, cancellationToken);
                return Results.Ok(response);
            }
            catch (HttpOperationException exception) when (exception.Response.StatusCode is HttpStatusCode.NotFound)
            {
                return Results.NotFound(new
                {
                    error = $"The pod '{request.PodName}' was not found."
                });
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new
                {
                    error = exception.Message
                });
            }
        });

        group.MapGet("/pods/logs/stream", async (
            HttpContext httpContext,
            PairingSessionRegistry sessions,
            PreviewReadOnlyStreamLimiter previewLimiter,
            KubePodLogStreamService podLogStreamService,
            CancellationToken cancellationToken) =>
        {
            if (!httpContext.WebSockets.IsWebSocketRequest)
            {
                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                await httpContext.Response.WriteAsJsonAsync(new
                {
                    error = "The pod log stream endpoint requires a WebSocket request."
                }, cancellationToken);

                return;
            }

            if (!TryParsePodLogStreamRequest(httpContext.Request, out var wsTicket, out var logRequest, out var errorMessage))
            {
                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                await httpContext.Response.WriteAsJsonAsync(new
                {
                    error = errorMessage
                }, cancellationToken);

                return;
            }

            var origin = httpContext.Request.Headers.Origin.ToString();
            var authorization = sessions.AuthorizeWebSocketTicket(wsTicket, origin);

            if (!authorization.Success)
            {
                httpContext.Response.StatusCode = authorization.StatusCode;
                await httpContext.Response.WriteAsJsonAsync(new
                {
                    error = authorization.ErrorMessage
                }, cancellationToken);

                return;
            }

            var previewLimit = previewLimiter.TryAcquire(authorization.Session!, PreviewReadOnlyStreamKind.PodLog);

            if (!previewLimit.Success)
            {
                httpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await httpContext.Response.WriteAsJsonAsync(new
                {
                    error = previewLimit.ErrorMessage
                }, cancellationToken);

                return;
            }

            using var previewLease = previewLimit.Lease;
            using var webSocket = await httpContext.WebSockets.AcceptWebSocketAsync();

            try
            {
                await podLogStreamService.StreamAsync(
                    logRequest!,
                    (message, token) => SendWebSocketJsonAsync(webSocket, message, token),
                    cancellationToken);
            }
            catch (ArgumentException exception)
            {
                await SendWebSocketJsonAsync(
                    webSocket,
                    new KubePodLogStreamMessage(
                        MessageType: KubePodLogStreamMessageType.Error,
                        OccurredAtUtc: DateTimeOffset.UtcNow,
                        Snapshot: null,
                        AppendContent: null,
                        ErrorMessage: exception.Message),
                    cancellationToken);
            }
            catch (Exception exception)
            {
                if (exception is OperationCanceledException)
                {
                    return;
                }

                await SendWebSocketJsonAsync(
                    webSocket,
                    new KubePodLogStreamMessage(
                        MessageType: KubePodLogStreamMessageType.Error,
                        OccurredAtUtc: DateTimeOffset.UtcNow,
                        Snapshot: null,
                        AppendContent: null,
                        ErrorMessage: exception.Message),
                    cancellationToken);
            }
            finally
            {
                if (webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await webSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Pod log stream closed.",
                        CancellationToken.None);
                }
            }
        });

        group.MapGet("/live/stream", async (
            HttpContext httpContext,
            PairingSessionRegistry sessions,
            PreviewReadOnlyStreamLimiter previewLimiter,
            [FromServices] KubeLiveSurfaceService liveSurfaceService,
            CancellationToken cancellationToken) =>
        {
            if (!httpContext.WebSockets.IsWebSocketRequest)
            {
                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                await httpContext.Response.WriteAsJsonAsync(new
                {
                    error = "The live surface endpoint requires a WebSocket request."
                }, cancellationToken);

                return;
            }

            if (!TryParseLiveSurfaceRequest(httpContext.Request, out var wsTicket, out var liveSurfaceRequest, out var errorMessage))
            {
                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                await httpContext.Response.WriteAsJsonAsync(new
                {
                    error = errorMessage
                }, cancellationToken);

                return;
            }

            var origin = httpContext.Request.Headers.Origin.ToString();
            var authorization = sessions.AuthorizeWebSocketTicket(wsTicket, origin);

            if (!authorization.Success)
            {
                httpContext.Response.StatusCode = authorization.StatusCode;
                await httpContext.Response.WriteAsJsonAsync(new
                {
                    error = authorization.ErrorMessage
                }, cancellationToken);

                return;
            }

            var previewLimit = previewLimiter.TryAcquire(authorization.Session!, PreviewReadOnlyStreamKind.ResourceWatch);

            if (!previewLimit.Success)
            {
                httpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await httpContext.Response.WriteAsJsonAsync(new
                {
                    error = previewLimit.ErrorMessage
                }, cancellationToken);

                return;
            }

            using var previewLease = previewLimit.Lease;
            using var webSocket = await httpContext.WebSockets.AcceptWebSocketAsync();

            try
            {
                await liveSurfaceService.StreamAsync(
                    liveSurfaceRequest!,
                    (message, token) => SendWebSocketJsonAsync(webSocket, message, token),
                    cancellationToken);
            }
            catch (ArgumentException exception)
            {
                await SendWebSocketJsonAsync(
                    webSocket,
                    new KubeLiveSurfaceStreamMessage(
                        MessageType: KubeLiveSurfaceStreamMessageType.Error,
                        OccurredAtUtc: DateTimeOffset.UtcNow,
                        Snapshot: null,
                        ErrorMessage: exception.Message),
                    cancellationToken);
            }
            catch (Exception exception)
            {
                if (exception is OperationCanceledException)
                {
                    return;
                }

                await SendWebSocketJsonAsync(
                    webSocket,
                    new KubeLiveSurfaceStreamMessage(
                        MessageType: KubeLiveSurfaceStreamMessageType.Error,
                        OccurredAtUtc: DateTimeOffset.UtcNow,
                        Snapshot: null,
                        ErrorMessage: exception.Message),
                    cancellationToken);
            }
            finally
            {
                if (webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await webSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Live surface stream closed.",
                        CancellationToken.None);
                }
            }
        });

        group.MapGet("/resources/watch", async (
            HttpContext httpContext,
            PairingSessionRegistry sessions,
            PreviewReadOnlyStreamLimiter previewLimiter,
            KubeResourceWatchService watchService,
            CancellationToken cancellationToken) =>
        {
            if (!httpContext.WebSockets.IsWebSocketRequest)
            {
                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                await httpContext.Response.WriteAsJsonAsync(new
                {
                    error = "The resource watch endpoint requires a WebSocket request."
                }, cancellationToken);

                return;
            }

            if (!TryParseWatchRequest(httpContext.Request, out var wsTicket, out var watchRequest, out var errorMessage))
            {
                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                await httpContext.Response.WriteAsJsonAsync(new
                {
                    error = errorMessage
                }, cancellationToken);

                return;
            }

            var origin = httpContext.Request.Headers.Origin.ToString();
            var authorization = sessions.AuthorizeWebSocketTicket(wsTicket, origin);

            if (!authorization.Success)
            {
                httpContext.Response.StatusCode = authorization.StatusCode;
                await httpContext.Response.WriteAsJsonAsync(new
                {
                    error = authorization.ErrorMessage
                }, cancellationToken);

                return;
            }

            var previewLimit = previewLimiter.TryAcquire(authorization.Session!, PreviewReadOnlyStreamKind.ResourceWatch);

            if (!previewLimit.Success)
            {
                httpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await httpContext.Response.WriteAsJsonAsync(new
                {
                    error = previewLimit.ErrorMessage
                }, cancellationToken);

                return;
            }

            using var previewLease = previewLimit.Lease;
            using var webSocket = await httpContext.WebSockets.AcceptWebSocketAsync();

            try
            {
                await watchService.WatchAsync(
                    watchRequest!,
                    (message, token) => SendWebSocketJsonAsync(webSocket, message, token),
                    cancellationToken);
            }
            catch (ArgumentException exception)
            {
                await SendWebSocketJsonAsync(
                    webSocket,
                    new KubeResourceWatchMessage(
                        MessageType: KubeResourceWatchMessageType.Error,
                        OccurredAtUtc: DateTimeOffset.UtcNow,
                        Snapshot: null,
                        ErrorMessage: exception.Message),
                    cancellationToken);
            }
            catch (Exception exception)
            {
                if (exception is OperationCanceledException)
                {
                    return;
                }

                await SendWebSocketJsonAsync(
                    webSocket,
                    new KubeResourceWatchMessage(
                        MessageType: KubeResourceWatchMessageType.Error,
                        OccurredAtUtc: DateTimeOffset.UtcNow,
                        Snapshot: null,
                        ErrorMessage: exception.Message),
                    cancellationToken);
            }
            finally
            {
                if (webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await webSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Watch stream closed.",
                        CancellationToken.None);
                }
            }
        });

        return endpoints;
    }

    private static IResult? TryRequireAuthenticatedHttpSession(
        HttpContext httpContext,
        out AuthenticatedAgentSession? session)
    {
        if (httpContext.TryGetAuthenticatedAgentSession(out session))
        {
            return null;
        }

        return Results.Json(
            new { error = "The authenticated agent session was not available in the current request context." },
            statusCode: StatusCodes.Status500InternalServerError);
    }

    private static IResult? TryRequireInteractiveMutationSession(
        HttpContext httpContext,
        out AuthenticatedAgentSession? session)
    {
        var authorizationFailure = TryRequireAuthenticatedHttpSession(httpContext, out session);

        if (authorizationFailure is not null)
        {
            return authorizationFailure;
        }

        if (session!.GrantedMode is not OriginAccessClass.Interactive)
        {
            return Results.Json(
                new { error = "This session is read-only preview only and cannot execute mutations." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        var csrfHeader = httpContext.Request.Headers["X-Kuberkynesis-Csrf"].ToString();

        if (string.IsNullOrWhiteSpace(csrfHeader) ||
            !string.Equals(csrfHeader, session.CsrfToken, StringComparison.Ordinal))
        {
            return Results.Json(
                new { error = "A valid X-Kuberkynesis-Csrf header is required for mutation requests." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        return null;
    }

    private static IResult? TryRequireInteractiveExecSession(
        HttpContext httpContext,
        out AuthenticatedAgentSession? session)
    {
        var authorizationFailure = TryRequireAuthenticatedHttpSession(httpContext, out session);

        if (authorizationFailure is not null)
        {
            return authorizationFailure;
        }

        if (session!.GrantedMode is not OriginAccessClass.Interactive)
        {
            return Results.Json(
                new { error = "This session is read-only preview only and cannot open interactive exec shells." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        var csrfHeader = httpContext.Request.Headers["X-Kuberkynesis-Csrf"].ToString();

        if (string.IsNullOrWhiteSpace(csrfHeader) ||
            !string.Equals(csrfHeader, session.CsrfToken, StringComparison.Ordinal))
        {
            return Results.Json(
                new { error = "A valid X-Kuberkynesis-Csrf header is required before the browser can open or control an exec shell." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        return null;
    }

    private static string BuildKubernetesForbiddenMutationMessage(KubeActionExecuteRequest request)
    {
        var resourceScope = string.IsNullOrWhiteSpace(request.Namespace)
            ? $"{request.Kind}/{request.Name}"
            : $"{request.Kind}/{request.Name} in namespace '{request.Namespace.Trim()}'";

        return $"Kubernetes RBAC denied this mutation for {resourceScope}. Use a kubeconfig or context with update permission for the requested action.";
    }

    private static string BuildKubernetesForbiddenExecMessage(KubePodExecStartRequest request)
    {
        return $"Kubernetes RBAC denied exec for Pod/{request.PodName.Trim()} in namespace '{request.Namespace.Trim()}'. Use a kubeconfig or context with pods/exec permission for the target container.";
    }

    private static bool TryParsePodLogStreamRequest(
        HttpRequest request,
        out string? wsTicket,
        out KubePodLogRequest? logRequest,
        out string? errorMessage)
    {
        wsTicket = request.Query["wsTicket"].ToString();
        logRequest = null;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(wsTicket))
        {
            errorMessage = "A wsTicket query value is required.";
            return false;
        }

        var contextName = request.Query["context"].ToString();
        var namespaceName = request.Query["namespace"].ToString();
        var podName = request.Query["pod"].ToString();

        if (string.IsNullOrWhiteSpace(contextName))
        {
            errorMessage = "A kube context query value is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(namespaceName))
        {
            errorMessage = "A namespace query value is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(podName))
        {
            errorMessage = "A pod query value is required.";
            return false;
        }

        var tailLines = 200;
        var tailLinesValue = request.Query["tailLines"].ToString();

        if (!string.IsNullOrWhiteSpace(tailLinesValue) &&
            (!int.TryParse(tailLinesValue, out tailLines) || tailLines < 1 || tailLines > 1000))
        {
            errorMessage = "The log stream tail line count must be between 1 and 1000.";
            return false;
        }

        var containerName = request.Query["container"].ToString();

        logRequest = new KubePodLogRequest
        {
            ContextName = contextName.Trim(),
            Namespace = namespaceName.Trim(),
            PodName = podName.Trim(),
            ContainerName = string.IsNullOrWhiteSpace(containerName) ? null : containerName.Trim(),
            TailLines = tailLines
        };

        return true;
    }

    private static bool TryParsePodExecStreamRequest(
        HttpRequest request,
        out string? wsTicket,
        out string? sessionId,
        out string? errorMessage)
    {
        wsTicket = request.Query["wsTicket"].ToString();
        sessionId = request.Query["sessionId"].ToString();
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(wsTicket))
        {
            errorMessage = "A wsTicket query value is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            errorMessage = "A sessionId query value is required.";
            return false;
        }

        return true;
    }

    private static bool TryParseActionExecutionStreamRequest(
        HttpRequest request,
        out string? wsTicket,
        out string? executionId,
        out string? errorMessage)
    {
        wsTicket = request.Query["wsTicket"].ToString();
        executionId = request.Query["executionId"].ToString();
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(wsTicket))
        {
            errorMessage = "A wsTicket query value is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(executionId))
        {
            errorMessage = "An executionId query value is required.";
            return false;
        }

        return true;
    }

    private static bool TryParseLiveSurfaceRequest(
        HttpRequest request,
        out string? wsTicket,
        out KubeLiveSurfaceQueryRequest? liveSurfaceRequest,
        out string? errorMessage)
    {
        wsTicket = request.Query["wsTicket"].ToString();
        liveSurfaceRequest = null;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(wsTicket))
        {
            errorMessage = "A wsTicket query value is required.";
            return false;
        }

        var contextName = request.Query["context"].ToString();
        var kindValue = request.Query["kind"].ToString();
        var resourceName = request.Query["name"].ToString();

        if (string.IsNullOrWhiteSpace(contextName))
        {
            errorMessage = "A kube context query value is required.";
            return false;
        }

        if (!Enum.TryParse<KubeResourceKind>(kindValue, ignoreCase: true, out var kind))
        {
            errorMessage = "A valid Kubernetes resource kind is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(resourceName))
        {
            errorMessage = "A resource name query value is required.";
            return false;
        }

        var limit = 20;
        var limitValue = request.Query["limit"].ToString();

        if (!string.IsNullOrWhiteSpace(limitValue) &&
            (!int.TryParse(limitValue, out limit) || limit < 1 || limit > 40))
        {
            errorMessage = "The live surface limit must be between 1 and 40.";
            return false;
        }

        liveSurfaceRequest = new KubeLiveSurfaceQueryRequest
        {
            ContextName = contextName.Trim(),
            Kind = kind,
            Namespace = request.Query["namespace"].ToString(),
            Name = resourceName.Trim(),
            Limit = limit
        };

        return true;
    }

    private static bool TryParseWatchRequest(
        HttpRequest request,
        out string? wsTicket,
        out KubeResourceWatchRequest? watchRequest,
        out string? errorMessage)
    {
        wsTicket = request.Query["wsTicket"].ToString();
        watchRequest = null;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(wsTicket))
        {
            errorMessage = "A wsTicket query value is required.";
            return false;
        }

        var kindValue = request.Query["kind"].ToString();

        if (!Enum.TryParse<KubeResourceKind>(kindValue, ignoreCase: true, out var kind))
        {
            errorMessage = "A valid Kubernetes resource kind is required.";
            return false;
        }

        var limit = 100;
        var limitValue = request.Query["limit"].ToString();

        if (!string.IsNullOrWhiteSpace(limitValue) &&
            (!int.TryParse(limitValue, out limit) || limit < 1 || limit > 500))
        {
            errorMessage = "The watch limit must be between 1 and 500.";
            return false;
        }

        var contexts = request.Query["context"]
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value?.Trim() ?? string.Empty)
            .ToArray();

        watchRequest = new KubeResourceWatchRequest
        {
            Kind = kind,
            Contexts = contexts,
            Namespace = request.Query["namespace"].ToString(),
            Search = request.Query["search"].ToString(),
            Limit = limit
        };

        return true;
    }

    private static Task SendWebSocketJsonAsync<TPayload>(WebSocket webSocket, TPayload payload, CancellationToken cancellationToken)
    {
        if (webSocket.State is not WebSocketState.Open)
        {
            return Task.CompletedTask;
        }

        var json = JsonSerializer.Serialize(payload, WebSocketSerializerOptions);
        var buffer = Encoding.UTF8.GetBytes(json);

        return webSocket.SendAsync(
            new ArraySegment<byte>(buffer),
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken);
    }

    private static JsonSerializerOptions CreateWebSocketSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
