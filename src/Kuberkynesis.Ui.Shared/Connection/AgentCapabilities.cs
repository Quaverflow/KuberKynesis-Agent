namespace Kuberkynesis.Ui.Shared.Connection;

public sealed record AgentCapabilities(
    bool Mutations,
    bool Logs,
    bool Exec,
    bool PortForward,
    bool LiveStreams);
