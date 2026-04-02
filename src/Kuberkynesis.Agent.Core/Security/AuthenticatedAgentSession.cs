using Kuberkynesis.Ui.Shared.Connection;

namespace Kuberkynesis.Agent.Core.Security;

public sealed record AuthenticatedAgentSession(
    string SessionToken,
    string CsrfToken,
    OriginAccessClass GrantedMode,
    string Origin,
    string AppVersion,
    DateTimeOffset ExpiresAtUtc);
