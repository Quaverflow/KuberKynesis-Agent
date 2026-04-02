namespace Kuberkynesis.Ui.Shared.Connection;

public sealed record PairResponse(
    string SessionToken,
    DateTimeOffset SessionExpiresAtUtc,
    string CsrfToken,
    string AgentInstanceId,
    OriginAccessClass GrantedMode);
