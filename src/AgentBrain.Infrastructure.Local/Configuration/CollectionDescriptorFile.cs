using System.Text;
using System.Text.Json;

namespace AgentBrain.Infrastructure.Local.Configuration;

/// <summary>The <c>collection.json</c> written for a new general-knowledge collection (<c>brain collection create</c>, <c>brain-server collections add</c>).</summary>
public static class CollectionDescriptorFile
{
    public const string FileName = "collection.json";

    /// <summary>A general-scope descriptor; technologies become the default applicability of every entry.</summary>
    public static string Create(string id, string title, IReadOnlyList<string> technologies, bool readOnly = false)
    {
        ArgumentNullException.ThrowIfNull(technologies);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema", 1);
            writer.WriteString("id", id);
            writer.WriteString("title", title);
            if (readOnly)
            {
                writer.WriteBoolean("readOnly", true);
            }

            writer.WriteStartObject("defaults");
            writer.WriteStartObject("applies");
            writer.WriteString("scope", "general");
            if (technologies.Count > 0)
            {
                writer.WriteStartArray("technologies");
                foreach (var technology in technologies)
                {
                    writer.WriteStringValue(technology);
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    /// <summary>The <c>id</c> a descriptor declares, or null when it is missing or unreadable.</summary>
    public static string? ReadId(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return document.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A readable title from an ID: <c>library.unity</c> → <c>Library Unity</c>.</summary>
    public static string TitleFrom(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return string.Join(' ', id.Split('.', '-').Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]));
    }
}
