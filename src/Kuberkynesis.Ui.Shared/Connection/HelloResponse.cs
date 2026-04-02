namespace Kuberkynesis.Ui.Shared.Connection;

public sealed record HelloResponse(
    string AgentInstanceId,
    string AgentVersion,
    bool PairingRequired,
    PairingMode PairingMode,
    string Nonce,
    AgentCapabilities Capabilities,
    IReadOnlyList<string> AllowedOrigins,
    string PreviewOriginPattern);
