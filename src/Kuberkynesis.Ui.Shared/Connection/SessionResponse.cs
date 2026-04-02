namespace Kuberkynesis.Ui.Shared.Connection;

public sealed record SessionResponse(
    string AgentInstanceId,
    DateTimeOffset SessionExpiresAtUtc,
    OriginAccessClass GrantedMode,
    string Origin,
    string AppVersion);
