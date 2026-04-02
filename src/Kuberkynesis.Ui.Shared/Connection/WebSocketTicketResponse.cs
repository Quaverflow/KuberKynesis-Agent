namespace Kuberkynesis.Ui.Shared.Connection;

public sealed record WebSocketTicketResponse(
    string Ticket,
    DateTimeOffset ExpiresAtUtc);
