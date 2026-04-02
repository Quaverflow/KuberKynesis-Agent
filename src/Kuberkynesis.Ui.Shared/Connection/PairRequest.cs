using System.ComponentModel.DataAnnotations;

namespace Kuberkynesis.Ui.Shared.Connection;

public sealed record PairRequest
{
    [Required]
    public string Nonce { get; init; } = string.Empty;

    [Required]
    public string AppVersion { get; init; } = string.Empty;

    [Required]
    public string PairingCode { get; init; } = string.Empty;

    [Required]
    public string Origin { get; init; } = string.Empty;

    public OriginAccessClass RequestedMode { get; init; } = OriginAccessClass.Interactive;

    public bool TakeoverInteractiveSession { get; init; }
}
