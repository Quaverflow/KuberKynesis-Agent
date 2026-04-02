using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kuberkynesis.Agent.Kube;

internal static class KubeRawManifestFormatter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static string CreateJson(JsonNode? node)
    {
        return node?.ToJsonString(JsonOptions) ?? "{}";
    }

    public static string CreateYaml(JsonNode? node)
    {
        var builder = new StringBuilder();
        WriteYaml(node, builder, indentLevel: 0, key: null);
        return builder.ToString().TrimEnd();
    }

    private static void WriteYaml(JsonNode? node, StringBuilder builder, int indentLevel, string? key)
    {
        var indent = new string(' ', indentLevel * 2);

        switch (node)
        {
            case JsonObject jsonObject:
                if (key is not null)
                {
                    if (jsonObject.Count is 0)
                    {
                        builder.Append(indent).Append(EscapeKey(key)).Append(": {}").AppendLine();
                        break;
                    }

                    builder.Append(indent).Append(EscapeKey(key)).Append(':').AppendLine();
                }

                foreach (var property in jsonObject)
                {
                    WriteYaml(property.Value, builder, key is null ? indentLevel : indentLevel + 1, property.Key);
                }

                break;

            case JsonArray jsonArray:
                if (key is not null)
                {
                    if (jsonArray.Count is 0)
                    {
                        builder.Append(indent).Append(EscapeKey(key)).Append(": []").AppendLine();
                        break;
                    }

                    builder.Append(indent).Append(EscapeKey(key)).Append(':').AppendLine();
                }

                foreach (var item in jsonArray)
                {
                    var itemIndentLevel = key is null ? indentLevel : indentLevel + 1;
                    var itemIndent = new string(' ', itemIndentLevel * 2);

                    if (item is JsonObject or JsonArray)
                    {
                        builder.Append(itemIndent).Append('-').AppendLine();
                        WriteYaml(item, builder, itemIndentLevel + 1, null);
                    }
                    else
                    {
                        builder.Append(itemIndent).Append("- ").Append(FormatScalar(item)).AppendLine();
                    }
                }

                break;

            default:
                if (key is null)
                {
                    builder.Append(indent).Append(FormatScalar(node)).AppendLine();
                    break;
                }

                builder.Append(indent)
                    .Append(EscapeKey(key))
                    .Append(": ")
                    .Append(FormatScalar(node))
                    .AppendLine();
                break;
        }
    }

    private static string EscapeKey(string value)
    {
        return RequiresQuoting(value)
            ? JsonSerializer.Serialize(value)
            : value;
    }

    private static string FormatScalar(JsonNode? node)
    {
        if (node is null)
        {
            return "null";
        }

        if (node is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<bool>(out var booleanValue))
            {
                return booleanValue ? "true" : "false";
            }

            if (jsonValue.TryGetValue<long>(out var longValue))
            {
                return longValue.ToString(CultureInfo.InvariantCulture);
            }

            if (jsonValue.TryGetValue<double>(out var doubleValue))
            {
                return doubleValue.ToString(CultureInfo.InvariantCulture);
            }

            if (jsonValue.TryGetValue<string>(out var stringValue))
            {
                return RequiresQuoting(stringValue)
                    ? JsonSerializer.Serialize(stringValue)
                    : stringValue;
            }
        }

        return node.ToJsonString();
    }

    private static bool RequiresQuoting(string value)
    {
        return string.IsNullOrWhiteSpace(value) ||
               value.IndexOfAny([':', '#', '{', '}', '[', ']', ',', '&', '*', '?', '|', '-', '<', '>', '=', '!', '%', '@', '\\', '"', '\'']) >= 0 ||
               value.Contains('\n', StringComparison.Ordinal) ||
               value.StartsWith(" ", StringComparison.Ordinal) ||
               value.EndsWith(" ", StringComparison.Ordinal) ||
               bool.TryParse(value, out _) ||
               long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ||
               double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
    }
}
