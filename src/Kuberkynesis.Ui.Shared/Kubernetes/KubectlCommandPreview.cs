namespace Kuberkynesis.Ui.Shared.Kubernetes;

public sealed record KubectlCommandPreview(
    string Label,
    string Command,
    string? Notes = null,
    KubectlTransparencyKind TransparencyKind = KubectlTransparencyKind.Equivalent,
    string? TargetSummary = null,
    string? ScopeSummary = null,
    bool IsDryRun = false,
    string? RequestSummary = null);
