using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AgentBrain.Application.Abstractions.Proposals;
using AgentBrain.Application.Writing;
using AgentBrain.Domain.Identity;
using AgentBrain.Domain.Metadata;
using AgentBrain.Domain.Revisions;

namespace AgentBrain.Infrastructure.Local.Records;

/// <summary>
/// JSON forms of proposals and batch markers (AOT-safe: written with <see cref="Utf8JsonWriter"/>, read with
/// <see cref="JsonDocument"/>). Metadata is stored as its frontmatter tree, so reading it back goes through
/// the same validation as any entry.
/// </summary>
internal static class RecordJson
{
    public static string Write(Action<Utf8JsonWriter> write, bool indented = true)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = indented }))
        {
            write(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static void WriteChange(Utf8JsonWriter writer, string name, PreparedChange change)
    {
        writer.WriteStartObject(name);
        writer.WriteString("kind", change.Kind.ToString().ToLowerInvariant());
        writer.WriteString("target", change.Target.ToString());
        writer.WriteString("baseRevision", change.BaseRevision?.Value);
        if (change.Metadata is not null)
        {
            writer.WritePropertyName("metadata");
            using var metadata = JsonDocument.Parse(MetadataJson.Serialize(EntryMetadataWriter.ToNode(change.Metadata)));
            metadata.RootElement.WriteTo(writer);
        }

        writer.WriteString("body", change.Body);
        writer.WriteEndObject();
    }

    public static PreparedChange ReadChange(JsonElement element)
    {
        var kind = Enum.Parse<ChangeKind>(element.GetProperty("kind").GetString()!, ignoreCase: true);
        var target = EntryRef.Parse(element.GetProperty("target").GetString()!);
        var baseRevision = element.GetProperty("baseRevision").GetString() is { } rev ? Revision.Parse(rev) : null;
        var metadata = element.TryGetProperty("metadata", out var m)
            ? EntryMetadataReader.Read((MetadataMapping)MetadataJson.Deserialize(m.GetRawText())).Metadata
                ?? throw new InvalidDataException($"Stored metadata for {target} no longer validates.")
            : null;
        var body = element.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
        return new PreparedChange(kind, target, baseRevision, metadata, body);
    }

    public static void WriteAttribution(Utf8JsonWriter writer, string name, Attribution? attribution)
    {
        if (attribution is null)
        {
            writer.WriteNull(name);
            return;
        }

        writer.WriteStartObject(name);
        writer.WriteString("actor", attribution.Actor);
        writer.WriteString("via", attribution.Via);
        writer.WriteEndObject();
    }

    public static Attribution? ReadAttribution(JsonElement element, string name) =>
        element.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Object
            ? new Attribution(a.GetProperty("actor").GetString()!, a.GetProperty("via").GetString())
            : null;

    public static string Time(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    public static DateTimeOffset? ReadTime(JsonElement element, string name) =>
        element.TryGetProperty(name, out var t) && t.ValueKind == JsonValueKind.String
            ? DateTimeOffset.Parse(t.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            : null;
}
