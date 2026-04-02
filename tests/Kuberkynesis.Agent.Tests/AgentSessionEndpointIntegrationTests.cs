using System.IO;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using k8s.Autorest;
using Kuberkynesis.Agent.Core.Configuration;
using Kuberkynesis.Agent.Core.Security;
using Kuberkynesis.Agent.Kube;
using Kuberkynesis.Agent.Transport.Api;
using Kuberkynesis.Ui.Shared.Connection;
using Kuberkynesis.Ui.Shared.Kubernetes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kuberkynesis.Agent.Tests;

[Collection(AgentIntegrationCollection.Name)]
public sealed class AgentSessionEndpointIntegrationTests
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    [Theory]
    [InlineData("http://localhost:5173")]
    [InlineData("https://kuberkynesis.com")]
    [InlineData("https://kuberkynesis.pages.dev")]
    public async Task HelloAndPairEndpoints_GrantInteractiveSessionsForInteractiveOrigins(string origin)
    {
        await using var host = await StartHostAsync();

        using var helloResponse = await SendAsyncWithRetry(host.Client, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/v1/hello");
            request.Headers.Add("Origin", origin);
            return request;
        });

        Assert.Equal(HttpStatusCode.OK, helloResponse.StatusCode);
        var hello = await helloResponse.Content.ReadFromJsonAsync<HelloResponse>(SerializerOptions);
        Assert.NotNull(hello);
        Assert.Equal("^https://[a-z0-9-]+\\.kuberkynesis\\.pages\\.dev$", hello!.PreviewOriginPattern);
        Assert.Contains("http://localhost:5173", hello.AllowedOrigins, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("https://kuberkynesis.com", hello.AllowedOrigins, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("https://kuberkynesis.pages.dev", hello.AllowedOrigins, StringComparer.OrdinalIgnoreCase);

        var pairRequest = new PairRequest
        {
            Nonce = hello.Nonce,
            AppVersion = "1.0.0",
            PairingCode = ExtractPairingCode(host.App.Services),
            Origin = origin,
            RequestedMode = OriginAccessClass.Interactive
        };

        using var pairResponse = await SendAsyncWithRetry(host.Client, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/v1/pair");
            request.Headers.Add("Origin", origin);
            request.Content = JsonContent.Create(pairRequest, options: SerializerOptions);
            return request;
        });

        Assert.Equal(HttpStatusCode.OK, pairResponse.StatusCode);
        var pairPayload = await pairResponse.Content.ReadFromJsonAsync<PairResponse>(SerializerOptions);
        Assert.NotNull(pairPayload);
        Assert.Equal(OriginAccessClass.Interactive, pairPayload!.GrantedMode);
        Assert.Equal(GetRegistry(host.App.Services).AgentInstanceId, pairPayload.AgentInstanceId);
    }

    [Fact]
    public async Task PairEndpoint_GrantsReadonlyPreviewSessionsForPreviewOrigins()
    {
        await using var host = await StartHostAsync();
        const string previewOrigin = "https://lab-preview.kuberkynesis.pages.dev";

        var pairPayload = await PairAsync(host, previewOrigin);
        Assert.Equal(OriginAccessClass.ReadonlyPreview, pairPayload.GrantedMode);
    }

    [Fact]
    public async Task PairEndpoint_AllowsExplicitTakeoverOfAnExistingInteractiveSession()
    {
        await using var host = await StartHostAsync();
        const string origin = "http://localhost:5173";

        var firstPair = await PairAsync(host, origin);
        var secondPair = await PairAsync(host, origin, takeoverInteractiveSession: true);

        Assert.NotEqual(firstPair.SessionToken, secondPair.SessionToken);
    }

    [Fact]
    public async Task WebSocketTicketEndpoint_IssuesATicketForAnAuthenticatedSession()
    {
        await using var host = await StartHostAsync();
        const string origin = "http://localhost:5173";

        var pairPayload = await PairAsync(host, origin, takeoverInteractiveSession: true);

        using var ticketResponse = await SendAsyncWithRetry(host.Client, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/v1/session/ws-ticket");
            request.Headers.Add("Origin", origin);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", pairPayload.SessionToken);
            return request;
        });

        Assert.Equal(HttpStatusCode.OK, ticketResponse.StatusCode);
        var ticketPayload = await ticketResponse.Content.ReadFromJsonAsync<WebSocketTicketResponse>(SerializerOptions);
        Assert.NotNull(ticketPayload);
        Assert.StartsWith("wst_", ticketPayload!.Ticket, StringComparison.Ordinal);
        Assert.True(ticketPayload.ExpiresAtUtc > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task ExecuteEndpoint_RejectsMissingBearerTokens()
    {
        await using var host = await StartHostAsync();

        using var response = await SendAsyncWithRetry(host.Client, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/v1/actions/execute");
            request.Headers.Add("Origin", "http://localhost:5173");
            request.Content = JsonContent.Create(new KubeActionExecuteRequest(
                ContextName: "kind-kuberkynesis-lab",
                Kind: KubeResourceKind.Deployment,
                Namespace: "orders-prod",
                Name: "orders-api",
                Action: KubeActionKind.ScaleDeployment,
                TargetReplicas: 5,
                ConfirmationText: null), options: SerializerOptions);
            return request;
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ExecuteEndpoint_RejectsMissingCsrfHeadersForInteractiveSessions()
    {
        await using var host = await StartHostAsync();
        const string origin = "http://localhost:5173";

        var pairPayload = await PairAsync(host, origin, takeoverInteractiveSession: true);

        using var response = await SendAsyncWithRetry(host.Client, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/v1/actions/execute");
            request.Headers.Add("Origin", origin);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", pairPayload.SessionToken);
            request.Content = JsonContent.Create(new KubeActionExecuteRequest(
                ContextName: "kind-kuberkynesis-lab",
                Kind: KubeResourceKind.Deployment,
                Namespace: "orders-prod",
                Name: "orders-api",
                Action: KubeActionKind.ScaleDeployment,
                TargetReplicas: 5,
                ConfirmationText: null), options: SerializerOptions);
            return request;
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var errorPayload = await response.Content.ReadFromJsonAsync<ErrorPayload>(SerializerOptions);
        Assert.NotNull(errorPayload);
        Assert.Contains("csrf", errorPayload!.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteEndpoint_RejectsReadonlyPreviewSessionsEvenWithCsrfHeaders()
    {
        await using var host = await StartHostAsync();
        const string origin = "https://lab-preview.kuberkynesis.pages.dev";

        var pairPayload = await PairAsync(host, origin, OriginAccessClass.ReadonlyPreview);

        using var response = await SendAsyncWithRetry(host.Client, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/v1/actions/execute");
            request.Headers.Add("Origin", origin);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", pairPayload.SessionToken);
            request.Headers.Add("X-Kuberkynesis-Csrf", pairPayload.CsrfToken);
            request.Content = JsonContent.Create(new KubeActionExecuteRequest(
                ContextName: "kind-kuberkynesis-lab",
                Kind: KubeResourceKind.Deployment,
                Namespace: "orders-prod",
                Name: "orders-api",
                Action: KubeActionKind.ScaleDeployment,
                TargetReplicas: 5,
                ConfirmationText: null), options: SerializerOptions);
            return request;
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var errorPayload = await response.Content.ReadFromJsonAsync<ErrorPayload>(SerializerOptions);
        Assert.NotNull(errorPayload);
        Assert.Contains("read-only preview", errorPayload!.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteEndpoint_TranslatesKubernetesForbiddenIntoAReadableRbacError()
    {
        await using var host = await StartHostAsync(new ForbiddenExecutionService());
        const string origin = "http://localhost:5173";

        var pairPayload = await PairAsync(host, origin, takeoverInteractiveSession: true);

        using var response = await SendAsyncWithRetry(host.Client, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/v1/actions/execute");
            request.Headers.Add("Origin", origin);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", pairPayload.SessionToken);
            request.Headers.Add("X-Kuberkynesis-Csrf", pairPayload.CsrfToken);
            request.Content = JsonContent.Create(new KubeActionExecuteRequest(
                ContextName: "kind-kuberkynesis-lab",
                Kind: KubeResourceKind.Deployment,
                Namespace: "checkout-prod",
                Name: "checkout-api",
                Action: KubeActionKind.ScaleDeployment,
                TargetReplicas: 5,
                ConfirmationText: null), options: SerializerOptions);
            return request;
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var errorPayload = await response.Content.ReadFromJsonAsync<ErrorPayload>(SerializerOptions);
        Assert.NotNull(errorPayload);
        Assert.Contains("RBAC denied", errorPayload!.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("checkout-prod", errorPayload.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WorkspaceResolveEndpoint_NormalizesTheWorkspaceScopeAndReturnsWarnings()
    {
        var kubeConfigLoader = new StaticKubeConfigLoader(
            new KubeConfigLoadResult(
                Configuration: null,
                SourcePaths: [],
                CurrentContextName: "kind-kuberkynesis-lab",
                Contexts:
                [
                    new DiscoveredKubeContext(
                        Name: "kind-kuberkynesis-lab",
                        IsCurrent: true,
                        ClusterName: "kind-kuberkynesis-lab",
                        Namespace: "default",
                        UserName: "developer",
                        Server: "https://example.invalid",
                        Status: KubeContextStatus.Configured,
                        StatusMessage: null),
                    new DiscoveredKubeContext(
                        Name: "stale-prod",
                        IsCurrent: false,
                        ClusterName: "stale-prod",
                        Namespace: "default",
                        UserName: "developer",
                        Server: "https://example.invalid",
                        Status: KubeContextStatus.AuthenticationExpired,
                        StatusMessage: "Cluster credentials expired.")
                ],
                Warnings: []));

        await using var host = await StartHostAsync(kubeConfigLoader: kubeConfigLoader);
        const string origin = "http://localhost:5173";

        var pairPayload = await PairAsync(host, origin, takeoverInteractiveSession: true);

        using var response = await SendAsyncWithRetry(host.Client, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/v1/workspace/resolve");
            request.Headers.Add("Origin", origin);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", pairPayload.SessionToken);
            request.Content = JsonContent.Create(
                new KubeWorkspaceResolveRequest
                {
                    Kind = KubeResourceKind.Node,
                    Contexts = ["stale-prod", "missing-lab"],
                    Namespace = "orders-prod"
                },
                options: SerializerOptions);
            return request;
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<KubeWorkspaceResolveResponse>(SerializerOptions);
        Assert.NotNull(payload);
        Assert.Equal(["kind-kuberkynesis-lab"], payload!.ResolvedQuery.Contexts);
        Assert.Null(payload.ResolvedQuery.Namespace);
        Assert.Equal("stale-prod", Assert.Single(payload.UnavailableContexts).Name);
        Assert.Equal(["missing-lab"], payload.MissingContexts);
        Assert.True(payload.UsedCurrentContextFallback);
        Assert.True(payload.IgnoredNamespaceFilter);
    }

    [Theory]
    [InlineData(KubeResourceKind.Deployment, KubeActionKind.RestartDeploymentRollout, "orders-prod", "orders-api", null)]
    [InlineData(KubeResourceKind.Deployment, KubeActionKind.RollbackDeploymentRollout, "orders-prod", "orders-api", null)]
    [InlineData(KubeResourceKind.Pod, KubeActionKind.DeletePod, "orders-prod", "orders-api-abc123", null)]
    [InlineData(KubeResourceKind.StatefulSet, KubeActionKind.ScaleStatefulSet, "orders-prod", "orders-db", 2)]
    [InlineData(KubeResourceKind.DaemonSet, KubeActionKind.RestartDaemonSetRollout, "kube-system", "node-agent", null)]
    [InlineData(KubeResourceKind.Job, KubeActionKind.DeleteJob, "orders-prod", "orders-backfill", null)]
    [InlineData(KubeResourceKind.CronJob, KubeActionKind.SuspendCronJob, "orders-prod", "orders-nightly", null)]
    [InlineData(KubeResourceKind.CronJob, KubeActionKind.ResumeCronJob, "orders-prod", "orders-nightly", null)]
    [InlineData(KubeResourceKind.Node, KubeActionKind.CordonNode, null, "kuberkynesis-lab-worker", null)]
    [InlineData(KubeResourceKind.Node, KubeActionKind.UncordonNode, null, "kuberkynesis-lab-worker", null)]
    public async Task ExecuteEndpoint_AllowsAdditionalGuardedMutationSlices(
        KubeResourceKind kind,
        KubeActionKind action,
        string? namespaceName,
        string name,
        int? targetReplicas)
    {
        await using var host = await StartHostAsync(new SuccessfulExecutionService());
        const string origin = "http://localhost:5173";

        var pairPayload = await PairAsync(host, origin, takeoverInteractiveSession: true);

        using var response = await SendAsyncWithRetry(host.Client, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/v1/actions/execute");
            request.Headers.Add("Origin", origin);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", pairPayload.SessionToken);
            request.Headers.Add("X-Kuberkynesis-Csrf", pairPayload.CsrfToken);
            request.Content = JsonContent.Create(new KubeActionExecuteRequest(
                ContextName: "kind-kuberkynesis-lab",
                Kind: kind,
                Namespace: namespaceName,
                Name: name,
                Action: action,
                TargetReplicas: targetReplicas,
                ConfirmationText: null), options: SerializerOptions);
            return request;
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<KubeActionExecuteResponse>(SerializerOptions);
        Assert.NotNull(payload);
        Assert.Equal(action, payload!.Action);
        Assert.Equal(name, payload.Resource.Name);
    }

    [Fact]
    public async Task ExecuteStartEndpoint_StartsExecutionAndReturnsExecutionMetadata()
    {
        await using var host = await StartHostAsync(new SuccessfulExecutionService());
        const string origin = "http://localhost:5173";

        var pairPayload = await PairAsync(host, origin, takeoverInteractiveSession: true);
        var payload = await StartActionExecutionAsync(host, pairPayload, origin);

        Assert.StartsWith("axe_", payload.ExecutionId, StringComparison.Ordinal);
        Assert.True(payload.CanCancel);
        Assert.Equal(KubeActionKind.ScaleDeployment, payload.Action);
        Assert.Equal("orders-api", payload.Resource.Name);
        Assert.Equal("Starting", payload.StatusText);
    }

    [Fact]
    public async Task ActionExecutionStreamEndpoint_ReplaysProgressAndCompletionMessages()
    {
        await using var host = await StartHostAsync(new SuccessfulExecutionService());
        const string origin = "http://localhost:5173";

        var pairPayload = await PairAsync(host, origin);
        var startPayload = await StartActionExecutionAsync(host, pairPayload, origin);
        var wsTicket = await CreateWebSocketTicketAsync(host, pairPayload, origin);
        var messages = await ReadActionExecutionMessagesAsync(host, origin, wsTicket.Ticket, startPayload.ExecutionId);

        Assert.Contains(
            messages,
            message => message.MessageType is KubeActionExecutionStreamMessageType.Snapshot &&
                       string.Equals(message.Snapshot?.StatusText, "Starting", StringComparison.Ordinal));
        Assert.Contains(
            messages,
            message => message.MessageType is KubeActionExecutionStreamMessageType.Snapshot &&
                       string.Equals(message.Snapshot?.StatusText, "Submitting mutation", StringComparison.Ordinal));

        var completedMessage = Assert.Single(messages, message => message.MessageType is KubeActionExecutionStreamMessageType.Completed);
        Assert.NotNull(completedMessage.Result);
        Assert.Equal(KubeActionKind.ScaleDeployment, completedMessage.Result!.Action);
        Assert.Equal(KubeActionExecutionStatus.Succeeded, completedMessage.Result.Status);
    }

    [Fact]
    public async Task ExecuteCancelEndpoint_CancelsARunningExecutionAndStreamsCancelledOutcome()
    {
        await using var host = await StartHostAsync(new BlockingExecutionService());
        const string origin = "http://localhost:5173";

        var pairPayload = await PairAsync(host, origin);
        var startPayload = await StartActionExecutionAsync(host, pairPayload, origin);

        using var cancelResponse = await SendAsyncWithRetry(host.Client, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Delete, $"/v1/actions/execute/{Uri.EscapeDataString(startPayload.ExecutionId)}");
            request.Headers.Add("Origin", origin);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", pairPayload.SessionToken);
            request.Headers.Add("X-Kuberkynesis-Csrf", pairPayload.CsrfToken);
            return request;
        });

        Assert.Equal(HttpStatusCode.NoContent, cancelResponse.StatusCode);

        var wsTicket = await CreateWebSocketTicketAsync(host, pairPayload, origin);
        var messages = await ReadActionExecutionMessagesAsync(host, origin, wsTicket.Ticket, startPayload.ExecutionId);

        Assert.Contains(
            messages,
            message => message.MessageType is KubeActionExecutionStreamMessageType.Snapshot &&
                       string.Equals(message.Snapshot?.StatusText, "Cancelling", StringComparison.Ordinal));

        var cancelledMessage = Assert.Single(messages, message => message.MessageType is KubeActionExecutionStreamMessageType.Cancelled);
        Assert.NotNull(cancelledMessage.Result);
        Assert.Equal(KubeActionExecutionStatus.Cancelled, cancelledMessage.Result!.Status);
        Assert.Contains("cancelled", cancelledMessage.Result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecStartEndpoint_RejectsReadonlyPreviewSessionsEvenWithCsrfHeaders()
    {
        await using var host = await StartHostAsync(podExecRuntimeFactory: new FakePodExecRuntimeFactory());
        const string origin = "https://lab-preview.kuberkynesis.pages.dev";

        var pairPayload = await PairAsync(host, origin, OriginAccessClass.ReadonlyPreview);

        using var response = await SendAsyncWithRetry(host.Client, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/v1/exec/start");
            request.Headers.Add("Origin", origin);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", pairPayload.SessionToken);
            request.Headers.Add("X-Kuberkynesis-Csrf", pairPayload.CsrfToken);
            request.Content = JsonContent.Create(
                new KubePodExecStartRequest(
                    ContextName: "kind-kuberkynesis-lab",
                    Namespace: "orders-prod",
                    PodName: "orders-api-0",
                    Command: ["/bin/sh"],
                    ContainerName: "app"),
                options: SerializerOptions);
            return request;
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var errorPayload = await response.Content.ReadFromJsonAsync<ErrorPayload>(SerializerOptions);
        Assert.NotNull(errorPayload);
        Assert.Contains("read-only preview", errorPayload!.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecEndpoints_StartSendInputCancelAndReplayTheShellTranscript()
    {
        var runtimeFactory = new FakePodExecRuntimeFactory(blockAfterInitialOutput: true);
        await using var host = await StartHostAsync(podExecRuntimeFactory: runtimeFactory);
        const string origin = "http://localhost:5173";

        var pairPayload = await PairAsync(host, origin, takeoverInteractiveSession: true);
        var startPayload = await StartPodExecAsync(host, pairPayload, origin);

        Assert.StartsWith("pex_", startPayload.SessionId, StringComparison.Ordinal);
        Assert.Equal("app", startPayload.ContainerName);
        Assert.True(startPayload.CanCancel);
        Assert.True(startPayload.CanSendInput);

        using (var inputResponse = await SendAsyncWithRetry(host.Client, () =>
               {
                   var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/exec/{Uri.EscapeDataString(startPayload.SessionId)}/input");
                   request.Headers.Add("Origin", origin);
                   request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", pairPayload.SessionToken);
                   request.Headers.Add("X-Kuberkynesis-Csrf", pairPayload.CsrfToken);
                   request.Content = JsonContent.Create(
                       new KubePodExecInputRequest("echo hello", AppendNewline: true),
                       options: SerializerOptions);
                   return request;
               }))
        {
            Assert.Equal(HttpStatusCode.NoContent, inputResponse.StatusCode);
        }

        Assert.NotNull(runtimeFactory.LastRuntime);
        Assert.Equal($"echo hello{Environment.NewLine}", Assert.Single(runtimeFactory.LastRuntime!.Inputs));

        using (var cancelResponse = await SendAsyncWithRetry(host.Client, () =>
               {
                   var request = new HttpRequestMessage(HttpMethod.Delete, $"/v1/exec/{Uri.EscapeDataString(startPayload.SessionId)}");
                   request.Headers.Add("Origin", origin);
                   request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", pairPayload.SessionToken);
                   request.Headers.Add("X-Kuberkynesis-Csrf", pairPayload.CsrfToken);
                   return request;
               }))
        {
            Assert.Equal(HttpStatusCode.NoContent, cancelResponse.StatusCode);
        }

        var wsTicket = await CreateWebSocketTicketAsync(host, pairPayload, origin);
        var messages = await ReadPodExecMessagesAsync(host, origin, wsTicket.Ticket, startPayload.SessionId);

        Assert.Contains(
            messages,
            message => message.MessageType is KubePodExecStreamMessageType.Snapshot &&
                       string.Equals(message.Snapshot?.StatusText, "Opening shell", StringComparison.Ordinal));
        Assert.Contains(
            messages,
            message => message.MessageType is KubePodExecStreamMessageType.Snapshot &&
                       string.Equals(message.Snapshot?.StatusText, "Shell ready", StringComparison.Ordinal));
        Assert.Contains(
            messages,
            message => message.MessageType is KubePodExecStreamMessageType.Output &&
                       string.Equals(message.OutputFrame?.Text, "connected\n", StringComparison.Ordinal));

        var cancelledMessage = Assert.Single(messages, message => message.MessageType is KubePodExecStreamMessageType.Cancelled);
        Assert.NotNull(cancelledMessage.Snapshot);
        Assert.Contains("close", cancelledMessage.Snapshot!.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteSessionEndpoint_ReleasesTheInteractiveSessionForRePairing()
    {
        await using var host = await StartHostAsync();
        const string origin = "http://localhost:5173";

        var firstPair = await PairAsync(host, origin);

        using var deleteResponse = await SendAsyncWithRetry(host.Client, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Delete, "/v1/session");
            request.Headers.Add("Origin", origin);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", firstPair.SessionToken);
            return request;
        });

        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var secondPair = await PairAsync(host, origin);

        Assert.NotEqual(firstPair.SessionToken, secondPair.SessionToken);
    }

    [Fact]
    public async Task SessionReleaseEndpoint_SchedulesReleaseForImmediateRePairing()
    {
        await using var host = await StartHostAsync();
        const string origin = "http://localhost:5173";

        var firstPair = await PairAsync(host, origin);

        using var releaseResponse = await SendAsyncWithRetry(host.Client, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/v1/session/release");
            request.Headers.Add("Origin", origin);
            request.Content = new StringContent(firstPair.SessionToken, Encoding.UTF8, "text/plain");
            return request;
        });

        Assert.Equal(HttpStatusCode.NoContent, releaseResponse.StatusCode);

        var secondPair = await PairAsync(host, origin);

        Assert.NotEqual(firstPair.SessionToken, secondPair.SessionToken);
    }

    [Fact]
    public async Task HelloEndpoint_RejectsDisallowedOrigins()
    {
        await using var host = await StartHostAsync();

        using var response = await SendAsyncWithRetry(host.Client, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/v1/hello");
            request.Headers.Add("Origin", "https://evil.example");
            return request;
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var errorPayload = await response.Content.ReadFromJsonAsync<ErrorPayload>(SerializerOptions);
        Assert.NotNull(errorPayload);
        Assert.Contains("not allowed", errorPayload!.Error, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<TestAgentHost> StartHostAsync(
        IKubeActionExecutionService? actionExecutionService = null,
        IKubeConfigLoader? kubeConfigLoader = null,
        IKubePodExecRuntimeFactory? podExecRuntimeFactory = null)
    {
        var port = ReserveFreePort();
        var baseAddress = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
            ApplicationName = typeof(AgentSessionEndpointIntegrationTests).Assembly.FullName,
            ContentRootPath = AppContext.BaseDirectory
        });

        builder.WebHost.UseUrls(baseAddress);
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });

        var runtimeOptions = new AgentRuntimeOptions
        {
            PublicUrl = baseAddress,
            Origins = new OriginOptions
            {
                Interactive =
                [
                    "http://localhost:5173",
                    "https://kuberkynesis.com",
                    "https://kuberkynesis.pages.dev",
                    "https://app.kuberkynesis.com"
                ],
                PreviewPattern = "^https://[a-z0-9-]+\\.kuberkynesis\\.pages\\.dev$"
            }
        };

        builder.Services.AddSingleton(runtimeOptions);
        builder.Services.AddSingleton<OriginAccessClassifier>();
        builder.Services.AddSingleton<PairingSessionRegistry>();
        builder.Services.AddSingleton<PreviewReadOnlyStreamLimiter>();
        builder.Services.AddSingleton<AgentDiagnosticsResponseFactory>();
        if (kubeConfigLoader is null)
        {
            builder.Services.AddSingleton<IKubeConfigLoader, FakeKubeConfigLoader>();
        }
        else
        {
            builder.Services.AddSingleton(kubeConfigLoader);
        }
        builder.Services.AddSingleton<IKubectlAvailabilityProbe, FakeKubectlAvailabilityProbe>();
        builder.Services.AddSingleton<KubeBootstrapProbe>();
        builder.Services.AddSingleton<KubeContextDiscoveryService>();
        builder.Services.AddSingleton<KubeCustomResourceDefinitionService>();
        builder.Services.AddSingleton<KubeWorkspaceResolveService>();
        builder.Services.AddSingleton<KubeActionImpactEngine>();
        builder.Services.AddSingleton<KubeActionGuardrailEngine>();
        builder.Services.AddSingleton<KubeActionPreviewService>();
        if (actionExecutionService is null)
        {
            builder.Services.AddSingleton<IKubeActionExecutionService, KubeActionExecutionService>();
        }
        else
        {
            builder.Services.AddSingleton(actionExecutionService);
        }
        builder.Services.AddSingleton<KubeActionExecutionSessionCoordinator>();
        if (podExecRuntimeFactory is null)
        {
            builder.Services.AddSingleton<IKubePodExecRuntimeFactory>(new FakePodExecRuntimeFactory());
        }
        else
        {
            builder.Services.AddSingleton(podExecRuntimeFactory);
        }
        builder.Services.AddSingleton<KubePodExecSessionCoordinator>();
        builder.Services.AddSingleton<KubeResourceQueryService>();
        builder.Services.AddSingleton<KubeResourceDetailService>();
        builder.Services.AddSingleton<KubeResourceGraphService>();
        builder.Services.AddSingleton<KubeResourceTimelineService>();
        builder.Services.AddSingleton<KubeLiveSurfaceService>();
        builder.Services.AddSingleton<KubePodLogService>();
        builder.Services.AddSingleton<KubePodLogStreamService>();
        builder.Services.AddSingleton<KubeResourceWatchService>();

        var app = builder.Build();
        app.UseWebSockets();
        app.UseAgentBrowserAccess();
        app.UseAgentSessionValidation();
        app.MapAgentSessionEndpoints();
        await app.StartAsync();

        return new TestAgentHost(
            app,
            new HttpClient
            {
                BaseAddress = new Uri(baseAddress, UriKind.Absolute)
            });
    }

    private static async Task<PairResponse> PairAsync(
        TestAgentHost host,
        string origin,
        OriginAccessClass requestedMode = OriginAccessClass.Interactive,
        bool takeoverInteractiveSession = false)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var helloResponse = await SendAsyncWithRetry(host.Client, () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, "/v1/hello");
                request.Headers.Add("Origin", origin);
                return request;
            });
            helloResponse.EnsureSuccessStatusCode();
            var hello = await helloResponse.Content.ReadFromJsonAsync<HelloResponse>(SerializerOptions);

            var pairRequest = new PairRequest
            {
                Nonce = hello!.Nonce,
                AppVersion = "1.0.0",
                PairingCode = ExtractPairingCode(host.App.Services),
                Origin = origin,
                RequestedMode = requestedMode,
                TakeoverInteractiveSession = takeoverInteractiveSession
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/pair");
            request.Headers.Add("Origin", origin);
            request.Content = JsonContent.Create(pairRequest, options: SerializerOptions);

            HttpResponseMessage pairResponse;

            try
            {
                pairResponse = await host.Client.SendAsync(request);
            }
            catch (HttpRequestException exception) when (attempt < 2 && IsTransientLoopbackStartupAbort(exception))
            {
                await Task.Delay(40 * (attempt + 1));
                continue;
            }

            using (pairResponse)
            {
                if (pairResponse.StatusCode is HttpStatusCode.BadRequest && attempt < 2)
                {
                    var error = await pairResponse.Content.ReadFromJsonAsync<ErrorPayload>(SerializerOptions);

                    if (string.Equals(error?.Error, "The pairing nonce is missing or expired.", StringComparison.Ordinal))
                    {
                        await Task.Delay(20 * (attempt + 1));
                        continue;
                    }
                }

                pairResponse.EnsureSuccessStatusCode();
                return (await pairResponse.Content.ReadFromJsonAsync<PairResponse>(SerializerOptions))!;
            }
        }

        throw new InvalidOperationException("Pairing did not succeed after retrying with fresh nonces.");
    }

    private static async Task<KubeActionExecutionStartResponse> StartActionExecutionAsync(
        TestAgentHost host,
        PairResponse pairPayload,
        string origin,
        KubeActionExecuteRequest? request = null)
    {
        using var response = await SendAsyncWithRetry(host.Client, () =>
        {
            var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/actions/execute/start");
            httpRequest.Headers.Add("Origin", origin);
            httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", pairPayload.SessionToken);
            httpRequest.Headers.Add("X-Kuberkynesis-Csrf", pairPayload.CsrfToken);
            httpRequest.Content = JsonContent.Create(
                request ?? new KubeActionExecuteRequest(
                    ContextName: "kind-kuberkynesis-lab",
                    Kind: KubeResourceKind.Deployment,
                    Namespace: "orders-prod",
                    Name: "orders-api",
                    Action: KubeActionKind.ScaleDeployment,
                    TargetReplicas: 5,
                    ConfirmationText: null),
                options: SerializerOptions);
            return httpRequest;
        });

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<KubeActionExecutionStartResponse>(SerializerOptions))!;
    }

    private static async Task<WebSocketTicketResponse> CreateWebSocketTicketAsync(
        TestAgentHost host,
        PairResponse pairPayload,
        string origin)
    {
        using var response = await SendAsyncWithRetry(host.Client, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/v1/session/ws-ticket");
            request.Headers.Add("Origin", origin);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", pairPayload.SessionToken);
            return request;
        });

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<WebSocketTicketResponse>(SerializerOptions))!;
    }

    private static async Task<KubePodExecStartResponse> StartPodExecAsync(
        TestAgentHost host,
        PairResponse pairPayload,
        string origin,
        KubePodExecStartRequest? request = null)
    {
        using var response = await SendAsyncWithRetry(host.Client, () =>
        {
            var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/exec/start");
            httpRequest.Headers.Add("Origin", origin);
            httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", pairPayload.SessionToken);
            httpRequest.Headers.Add("X-Kuberkynesis-Csrf", pairPayload.CsrfToken);
            httpRequest.Content = JsonContent.Create(
                request ?? new KubePodExecStartRequest(
                    ContextName: "kind-kuberkynesis-lab",
                    Namespace: "orders-prod",
                    PodName: "orders-api-0",
                    Command: ["/bin/sh"],
                    ContainerName: "app"),
                options: SerializerOptions);
            return httpRequest;
        });

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<KubePodExecStartResponse>(SerializerOptions))!;
    }

    private static async Task<IReadOnlyList<KubeActionExecutionStreamMessage>> ReadActionExecutionMessagesAsync(
        TestAgentHost host,
        string origin,
        string webSocketTicket,
        string executionId)
    {
        var baseAddress = host.Client.BaseAddress ?? throw new InvalidOperationException("The test host base address is not configured.");
        var uriBuilder = new UriBuilder(baseAddress)
        {
            Scheme = string.Equals(baseAddress.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                ? Uri.UriSchemeWss
                : Uri.UriSchemeWs,
            Path = "/v1/actions/stream",
            Query = $"wsTicket={Uri.EscapeDataString(webSocketTicket)}&executionId={Uri.EscapeDataString(executionId)}"
        };

        using var webSocket = new ClientWebSocket();
        webSocket.Options.SetRequestHeader("Origin", origin);

        using var connectCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await webSocket.ConnectAsync(uriBuilder.Uri, connectCancellation.Token);

        var messages = new List<KubeActionExecutionStreamMessage>();
        var buffer = new byte[4096];

        while (true)
        {
            using var messageStream = new MemoryStream();
            WebSocketReceiveResult receiveResult;

            do
            {
                receiveResult = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), connectCancellation.Token);

                if (receiveResult.MessageType is WebSocketMessageType.Close)
                {
                    return messages;
                }

                messageStream.Write(buffer, 0, receiveResult.Count);
            }
            while (!receiveResult.EndOfMessage);

            if (receiveResult.MessageType is not WebSocketMessageType.Text)
            {
                continue;
            }

            var payload = JsonSerializer.Deserialize<KubeActionExecutionStreamMessage>(messageStream.ToArray(), SerializerOptions);
            Assert.NotNull(payload);
            messages.Add(payload!);
        }
    }

    private static async Task<IReadOnlyList<KubePodExecStreamMessage>> ReadPodExecMessagesAsync(
        TestAgentHost host,
        string origin,
        string webSocketTicket,
        string sessionId)
    {
        var baseAddress = host.Client.BaseAddress ?? throw new InvalidOperationException("The test host base address is not configured.");
        var uriBuilder = new UriBuilder(baseAddress)
        {
            Scheme = string.Equals(baseAddress.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                ? Uri.UriSchemeWss
                : Uri.UriSchemeWs,
            Path = "/v1/exec/stream",
            Query = $"wsTicket={Uri.EscapeDataString(webSocketTicket)}&sessionId={Uri.EscapeDataString(sessionId)}"
        };

        using var webSocket = new ClientWebSocket();
        webSocket.Options.SetRequestHeader("Origin", origin);

        using var connectCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await webSocket.ConnectAsync(uriBuilder.Uri, connectCancellation.Token);

        var messages = new List<KubePodExecStreamMessage>();
        var buffer = new byte[4096];

        while (true)
        {
            using var messageStream = new MemoryStream();
            WebSocketReceiveResult receiveResult;

            do
            {
                receiveResult = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), connectCancellation.Token);

                if (receiveResult.MessageType is WebSocketMessageType.Close)
                {
                    return messages;
                }

                messageStream.Write(buffer, 0, receiveResult.Count);
            }
            while (!receiveResult.EndOfMessage);

            if (receiveResult.MessageType is not WebSocketMessageType.Text)
            {
                continue;
            }

            var payload = JsonSerializer.Deserialize<KubePodExecStreamMessage>(messageStream.ToArray(), SerializerOptions);
            Assert.NotNull(payload);
            messages.Add(payload!);
        }
    }

    private static async Task<HttpResponseMessage> SendAsyncWithRetry(HttpClient client, Func<HttpRequestMessage> createRequest)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var request = createRequest();

            try
            {
                return await client.SendAsync(request);
            }
            catch (HttpRequestException exception) when (attempt < 2 && IsTransientLoopbackStartupAbort(exception))
            {
                await Task.Delay(40 * (attempt + 1));
            }
        }

        throw new InvalidOperationException("Unreachable retry state.");
    }

    private static bool IsTransientLoopbackStartupAbort(HttpRequestException exception)
    {
        return exception.InnerException is IOException or SocketException
               || exception.InnerException?.InnerException is SocketException;
    }

    private static int ReserveFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string ExtractPairingCode(IServiceProvider services)
    {
        var options = services.GetRequiredService<AgentRuntimeOptions>();
        var banner = GetRegistry(services).CreateStartupBanner(options);
        return Regex.Match(banner, "Pairing code: (?<code>[A-Z0-9]+)").Groups["code"].Value;
    }

    private static PairingSessionRegistry GetRegistry(IServiceProvider services)
    {
        return services.GetRequiredService<PairingSessionRegistry>();
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed record ErrorPayload(string Error);

    private sealed record TestAgentHost(WebApplication App, HttpClient Client) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }

    private sealed class FakeKubeConfigLoader : IKubeConfigLoader
    {
        public KubeConfigLoadResult Load()
        {
            return new KubeConfigLoadResult(
                Configuration: null,
                SourcePaths: [],
                CurrentContextName: null,
                Contexts: [],
                Warnings: []);
        }

        public k8s.Kubernetes CreateClient(KubeConfigLoadResult loadResult, string contextName)
        {
            throw new NotSupportedException("Kubernetes client creation is not used in the hello/pair integration tests.");
        }
    }

    private sealed class FakeKubectlAvailabilityProbe : IKubectlAvailabilityProbe
    {
        public KubectlAvailabilityProbeResult Probe()
        {
            return new KubectlAvailabilityProbeResult(
                IsAvailable: true,
                ClientVersion: "v1.test",
                Warning: null);
        }
    }

    private sealed class StaticKubeConfigLoader(KubeConfigLoadResult loadResult) : IKubeConfigLoader
    {
        public KubeConfigLoadResult Load()
        {
            return loadResult;
        }

        public k8s.Kubernetes CreateClient(KubeConfigLoadResult ignoredLoadResult, string contextName)
        {
            throw new NotSupportedException("The workspace-resolve integration test does not create live Kubernetes clients.");
        }
    }

    private sealed class ForbiddenExecutionService : IKubeActionExecutionService
    {
        public Task<KubeActionExecuteResponse> ExecuteAsync(KubeActionExecuteRequest request, CancellationToken cancellationToken)
        {
            return ExecuteAsync(request, reportProgress: null, cancellationToken);
        }

        public Task<KubeActionExecuteResponse> ExecuteAsync(
            KubeActionExecuteRequest request,
            Action<KubeActionExecutionProgressUpdate>? reportProgress,
            CancellationToken cancellationToken)
        {
            throw new HttpOperationException("Forbidden")
            {
                Response = new HttpResponseMessageWrapper(
                    new HttpResponseMessage(HttpStatusCode.Forbidden),
                    null)
            };
        }
    }

    private sealed class SuccessfulExecutionService : IKubeActionExecutionService
    {
        public Task<KubeActionExecuteResponse> ExecuteAsync(KubeActionExecuteRequest request, CancellationToken cancellationToken)
        {
            return ExecuteAsync(request, reportProgress: null, cancellationToken);
        }

        public Task<KubeActionExecuteResponse> ExecuteAsync(
            KubeActionExecuteRequest request,
            Action<KubeActionExecutionProgressUpdate>? reportProgress,
            CancellationToken cancellationToken)
        {
            reportProgress?.Invoke(new KubeActionExecutionProgressUpdate(
                "Submitting mutation",
                $"Synthetic execution in progress for {request.Name}."));

            return Task.FromResult(new KubeActionExecuteResponse(
                Action: request.Action,
                Resource: new KubeResourceIdentity(request.ContextName, request.Kind, request.Namespace, request.Name),
                Summary: $"{request.Action} executed for {request.Name}.",
                CompletedAtUtc: DateTimeOffset.UtcNow,
                Facts:
                [
                    new KubeActionPreviewFact("Action", request.Action.ToString())
                ],
                Notes:
                [
                    "Synthetic execution success for transport integration coverage."
                ],
                TransparencyCommands:
                [
                    new KubectlCommandPreview("Executed", "kubectl ...")
                ]));
        }
    }

    private sealed class BlockingExecutionService : IKubeActionExecutionService
    {
        public Task<KubeActionExecuteResponse> ExecuteAsync(KubeActionExecuteRequest request, CancellationToken cancellationToken)
        {
            return ExecuteAsync(request, reportProgress: null, cancellationToken);
        }

        public async Task<KubeActionExecuteResponse> ExecuteAsync(
            KubeActionExecuteRequest request,
            Action<KubeActionExecutionProgressUpdate>? reportProgress,
            CancellationToken cancellationToken)
        {
            reportProgress?.Invoke(new KubeActionExecutionProgressUpdate(
                "Submitting mutation",
                $"Synthetic execution is waiting for cancellation for {request.Name}."));

            await Task.Delay(Timeout.Infinite, cancellationToken);

            throw new InvalidOperationException("The synthetic blocking execution should only complete through cancellation.");
        }
    }

    private sealed class FakePodExecRuntimeFactory(bool blockAfterInitialOutput = false) : IKubePodExecRuntimeFactory
    {
        public FakePodExecRuntime? LastRuntime { get; private set; }

        public Task<IKubePodExecRuntime> CreateAsync(KubePodExecStartRequest request, CancellationToken cancellationToken)
        {
            LastRuntime = new FakePodExecRuntime(request, blockAfterInitialOutput);
            return Task.FromResult<IKubePodExecRuntime>(LastRuntime);
        }
    }

    private sealed class FakePodExecRuntime : IKubePodExecRuntime
    {
        public FakePodExecRuntime(KubePodExecStartRequest request, bool blockAfterInitialOutput)
        {
            Resource = new KubeResourceIdentity(request.ContextName, KubeResourceKind.Pod, request.Namespace, request.PodName);
            ContainerName = request.ContainerName;
            Command = request.Command;
            TransparencyCommands =
            [
                new KubectlCommandPreview(
                    Label: "Equivalent shell",
                    Command: $"kubectl --context {request.ContextName} -n {request.Namespace} exec pod/{request.PodName}" +
                             $"{(string.IsNullOrWhiteSpace(request.ContainerName) ? string.Empty : $" -c {request.ContainerName}")} -- {string.Join(" ", request.Command)}")
            ];
            StdOut = new ScriptedAsyncReadStream(["connected\n"], blockAfterInitialOutput);
            StdErr = new ScriptedAsyncReadStream([], blockAfterInitialOutput);
            Status = new ScriptedAsyncReadStream([], blockAfterInitialOutput);
        }

        public KubeResourceIdentity Resource { get; }

        public string? ContainerName { get; }

        public IReadOnlyList<string> Command { get; }

        public IReadOnlyList<KubectlCommandPreview> TransparencyCommands { get; }

        public Stream StdOut { get; }

        public Stream StdErr { get; }

        public Stream Status { get; }

        public List<string> Inputs { get; } = [];

        public Task SendInputAsync(string text, CancellationToken cancellationToken)
        {
            Inputs.Add(text);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            StdOut.Dispose();
            StdErr.Dispose();
            Status.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ScriptedAsyncReadStream(IEnumerable<string> chunks, bool waitForCancellationAfterChunks) : Stream
    {
        private readonly Queue<byte[]> pendingChunks = new(chunks.Select(Encoding.UTF8.GetBytes));
        private readonly bool waitForCancellationAfterChunks = waitForCancellationAfterChunks;
        private byte[]? activeChunk;
        private int activeOffset;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException("Synchronous reads are not used in the exec integration tests.");
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (activeChunk is null && pendingChunks.Count > 0)
            {
                activeChunk = pendingChunks.Dequeue();
                activeOffset = 0;
            }

            if (activeChunk is not null)
            {
                var count = Math.Min(buffer.Length, activeChunk.Length - activeOffset);
                activeChunk.AsMemory(activeOffset, count).CopyTo(buffer);
                activeOffset += count;

                if (activeOffset >= activeChunk.Length)
                {
                    activeChunk = null;
                    activeOffset = 0;
                }

                return count;
            }

            if (!waitForCancellationAfterChunks)
            {
                return 0;
            }

            var waitForCancellation = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellationRegistration = cancellationToken.Register(() => waitForCancellation.TrySetCanceled(cancellationToken));
            return await waitForCancellation.Task;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }
}
