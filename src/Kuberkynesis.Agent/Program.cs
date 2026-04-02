using System.Text.Json.Serialization;
using Kuberkynesis.Agent.Core.Configuration;
using Kuberkynesis.Agent.Core.Security;
using Kuberkynesis.Agent.Kube;
using Kuberkynesis.Agent.Startup;
using Kuberkynesis.Agent.Transport.Api;
using Kuberkynesis.LiveSurface.AspNetCore;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
var runtimeOptions = builder.Configuration.GetSection(AgentRuntimeOptions.SectionName).Get<AgentRuntimeOptions>() ?? new();
var startupOverrides = AgentStartupCliOverrides.Parse(args);
startupOverrides.ApplyTo(runtimeOptions);
var publicUrlSelection = AgentPublicUrlSelector.Resolve(
    runtimeOptions.PublicUrl,
    requireExactPort: startupOverrides.Port is not null || !string.Equals(runtimeOptions.PublicUrl, AgentRuntimeOptions.DefaultPublicUrl, StringComparison.OrdinalIgnoreCase));
runtimeOptions.PublicUrl = publicUrlSelection.SelectedUrl;

if (!string.IsNullOrWhiteSpace(startupOverrides.KubeConfigPath))
{
    Environment.SetEnvironmentVariable("KUBECONFIG", startupOverrides.KubeConfigPath);
}

builder.WebHost.UseUrls(runtimeOptions.PublicUrl);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddSingleton(runtimeOptions);
builder.Services.AddSingleton<OriginAccessClassifier>();
builder.Services.AddSingleton<PairingSessionRegistry>();
builder.Services.AddSingleton<PreviewReadOnlyStreamLimiter>();
builder.Services.AddSingleton<AgentUiLaunchService>();
builder.Services.AddSingleton<AgentStartupDiagnosticsPrinter>();
builder.Services.AddSingleton<AgentDiagnosticsResponseFactory>();
builder.Services.AddSingleton<IKubeConfigLoader, KubeConfigLoader>();
builder.Services.AddSingleton<IKubectlAvailabilityProbe, KubectlAvailabilityProbe>();
builder.Services.AddHttpClient<PrometheusMetricsSource>();
builder.Services.AddSingleton<KubeContextDiscoveryService>();
builder.Services.AddSingleton<KubeCustomResourceDefinitionService>();
builder.Services.AddSingleton<KubeWorkspaceResolveService>();
builder.Services.AddSingleton<KubeResourceQueryService>();
builder.Services.AddSingleton<KubeResourceWatchService>();
builder.Services.AddSingleton<KubeResourceDetailService>();
builder.Services.AddSingleton<KubeActionImpactEngine>();
builder.Services.AddSingleton<KubeActionGuardrailEngine>();
builder.Services.AddSingleton<KubeActionPreviewService>();
builder.Services.AddSingleton<IKubeActionExecutionService, KubeActionExecutionService>();
builder.Services.AddSingleton<KubeActionExecutionSessionCoordinator>();
builder.Services.AddSingleton<IKubePodExecRuntimeFactory, KubePodExecRuntimeFactory>();
builder.Services.AddSingleton<KubePodExecSessionCoordinator>();
builder.Services.AddSingleton<KubeResourceMetricsService>();
builder.Services.AddSingleton<KubeResourceGraphService>();
builder.Services.AddSingleton<KubeResourceTimelineService>();
builder.Services.AddSingleton<KubeLiveSurfaceService>();
builder.Services.AddSingleton<KubePodLogService>();
builder.Services.AddSingleton<KubePodLogStreamService>();
builder.Services.AddSingleton<KubeBootstrapProbe>();
builder.Services.AddKuberkynesisLiveSurface();

var app = builder.Build();

app.UseWebSockets();
app.UseAgentBrowserAccess();
app.UseAgentSessionValidation();

app.MapGet("/", () => Results.Ok(new
{
    service = "Kuberkynesis.Agent",
    status = "running",
    url = runtimeOptions.PublicUrl
}));

app.MapAgentSessionEndpoints();

app.Lifetime.ApplicationStarted.Register(() =>
{
    var sessions = app.Services.GetRequiredService<PairingSessionRegistry>();
    var uiLauncher = app.Services.GetRequiredService<AgentUiLaunchService>();
    var startupDiagnosticsPrinter = app.Services.GetRequiredService<AgentStartupDiagnosticsPrinter>();
    var kubeBootstrapProbe = app.Services.GetRequiredService<KubeBootstrapProbe>();

    sessions.PairingCodeRotated += notice =>
    {
        Console.WriteLine($"Pairing code rotated. New pairing code: {notice.PairingCode}");
        Console.WriteLine($"Code valid until: {notice.ExpiresAtUtc.ToLocalTime():O}");
    };

    if (!string.IsNullOrWhiteSpace(publicUrlSelection.Notice))
    {
        Console.WriteLine(publicUrlSelection.Notice);
    }

    Console.WriteLine(sessions.CreateStartupBanner(runtimeOptions));

    if (startupOverrides.EnableDiagnostics)
    {
        Console.WriteLine(startupDiagnosticsPrinter.BuildReport(
            runtimeOptions,
            kubeBootstrapProbe.Probe(),
            startupOverrides.KubeConfigPath));
    }

    uiLauncher.TryLaunchBrowser();
});

app.Run();
