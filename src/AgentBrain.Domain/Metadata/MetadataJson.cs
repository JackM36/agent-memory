using System.Buffers;
using System.Text;
using System.Text.Json;

namespace AgentBrain.Domain.Metadata;

/// <summary>
/// Lossless JSON form of a metadata tree, used where metadata is stored outside Markdown (the search
/// index, later the SQLite store). Scalars stay strings so the same reader validates both forms.
/// </summary>
public static class MetadataJson
{
    public static string Serialize(MetadataNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(writer, node);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static MetadataNode Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        return Read(document.RootElement);
    }

    private static void Write(Utf8JsonWriter writer, MetadataNode node)
    {
        switch (node)
        {
            case MetadataScalar scalar:
                writer.WriteStringValue(scalar.Value);
                break;
            case MetadataNull:
                writer.WriteNullValue();
                break;
            case MetadataSequence sequence:
                writer.WriteStartArray();
                foreach (var item in sequence.Items)
                {
                    Write(writer, item);
                }

                writer.WriteEndArray();
                break;
            case MetadataMapping mapping:
                writer.WriteStartObject();
                foreach (var (key, value) in mapping.Entries)
                {
                    writer.WritePropertyName(key);
                    Write(writer, value);
                }

                writer.WriteEndObject();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(node), node.GetType().Name, "Unknown metadata node.");
        }
    }

    private static MetadataNode Read(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => new MetadataMapping([.. element.EnumerateObject().Select(p => new KeyValuePair<string, MetadataNode>(p.Name, Read(p.Value)))]),
        JsonValueKind.Array => new MetadataSequence([.. element.EnumerateArray().Select(Read)]),
        JsonValueKind.Null => new MetadataNull(),
        JsonValueKind.String => new MetadataScalar(element.GetString()!),
        _ => new MetadataScalar(element.GetRawText()),
    };
}
