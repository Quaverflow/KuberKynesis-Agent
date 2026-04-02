using System.Text.Json.Serialization;

namespace Kuberkynesis.Ui.Shared.Connection;

public enum PairingMode
{
    [JsonStringEnumMemberName("code_entry")]
    CodeEntry
}
