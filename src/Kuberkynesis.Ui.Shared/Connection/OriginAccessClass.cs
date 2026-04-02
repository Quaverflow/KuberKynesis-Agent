using System.Text.Json.Serialization;

namespace Kuberkynesis.Ui.Shared.Connection;

public enum OriginAccessClass
{
    [JsonStringEnumMemberName("interactive")]
    Interactive,

    [JsonStringEnumMemberName("readonly_preview")]
    ReadonlyPreview,

    [JsonStringEnumMemberName("readonly_local")]
    ReadonlyLocal
}
