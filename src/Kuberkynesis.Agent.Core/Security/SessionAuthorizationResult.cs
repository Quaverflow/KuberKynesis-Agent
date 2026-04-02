namespace Kuberkynesis.Agent.Core.Security;

public sealed record SessionAuthorizationResult(
    bool Success,
    AuthenticatedAgentSession? Session,
    string? ErrorMessage,
    int StatusCode);
