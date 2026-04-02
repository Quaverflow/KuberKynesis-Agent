using Kuberkynesis.Ui.Shared.Connection;

namespace Kuberkynesis.Agent.Core.Security;

public sealed record PairingAttemptResult(
    bool Success,
    PairResponse? Response,
    string? ErrorMessage,
    int StatusCode);
