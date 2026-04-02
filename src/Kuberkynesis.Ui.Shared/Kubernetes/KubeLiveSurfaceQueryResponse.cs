using Kuberkynesis.LiveSurface;

namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubeLiveSurfaceQueryResponse(
    KubeResourceSummary Resource,
    string StreamName,
    string ScopeSummary,
    IReadOnlyList<LiveSurfaceEnvelope> Events,
    IReadOnlyList<KubeQueryWarning> Warnings,
    IReadOnlyList<KubectlCommandPreview>? TransparencyCommands = null);
