using System.Buffers;
using System.Text;
using System.Text.Json;
using AgentBrain.Domain.Entries;

namespace AgentBrain.Infrastructure.Sqlite;

/// <summary>Stores effective applicability as compact JSON (AOT-safe, no reflection).</summary>
internal static class ApplicabilityJson
{
    public static string Serialize(Applicability applicability)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("scope", WireNames.Scopes.Name(applicability.Scope));
            WriteList(writer, "projects", applicability.Projects);
            WriteList(writer, "technologies", applicability.Technologies);
            WriteList(writer, "platforms", applicability.Platforms);
            writer.WriteStartArray("packages");
            foreach (var package in applicability.Packages)
            {
                writer.WriteStartObject();
                writer.WriteString("name", package.Name);
                writer.WriteString("versions", package.Versions);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static Applicability Deserialize(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var scope = WireNames.Scopes.TryParse(root.GetProperty("scope").GetString(), out var parsed) ? parsed : throw new InvalidDataException("Index applicability has an unknown scope.");
        return new Applicability(
            scope,
            ReadList(root, "projects"),
            ReadList(root, "technologies"),
            [.. root.GetProperty("packages").EnumerateArray().Select(p => new PackageConstraint(p.GetProperty("name").GetString()!, p.GetProperty("versions").GetString()!))],
            ReadList(root, "platforms"));
    }

    private static void WriteList(Utf8JsonWriter writer, string name, IReadOnlyList<string> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static List<string> ReadList(JsonElement root, string name) =>
        [.. root.GetProperty(name).EnumerateArray().Select(v => v.GetString()!)];
}
