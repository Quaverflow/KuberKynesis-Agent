namespace Kuberkynesis.Ui.Shared.Connection;

public sealed record AgentTrustBoundarySummary(
    bool LoopbackOnlyBinding,
    bool KubeconfigUploadEnabled,
    bool RuntimeCloudSyncEnabled,
    bool RemoteExecutionEnabled,
    bool PublishedShareEnabled,
    bool SecretRevealEnabled,
    string BindingSummary,
    string ClusterAuthoritySummary,
    string RuntimeDataSummary,
    string SharingSummary,
    string SecretHandlingSummary);
