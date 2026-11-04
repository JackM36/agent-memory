using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBrain.Domain.Entries;
using AgentBrain.Domain.Metadata;

namespace AgentBrain.Domain.Revisions;

/// <summary>
/// Computes revisions from a canonical form (plan §5.11):
/// metadata as JSON with ordinally sorted keys and <c>x-</c> fields excluded, then the body with the
/// BOM removed, line endings normalised to LF and exactly one trailing newline. Storage location,
/// timestamps of the file system and formatting of the source never affect the result.
/// </summary>
public static class RevisionCalculator
{
    private const string Separator = "\n---\n";

    public static Revision Compute(EntryMetadata metadata, string body)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(body);
        var canonical = CanonicalMetadataJson(metadata) + Separator + NormalizeBody(body);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Revision.FromHash(Base32.Encode(hash));
    }

    public static string NormalizeBody(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var text = body.TrimStart('﻿').ReplaceLineEndings("\n").TrimEnd('\n');
        return text.Length == 0 ? "" : text + "\n";
    }

    public static string CanonicalMetadataJson(EntryMetadata metadata)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(writer, EntryMetadataWriter.ToNode(metadata, includeExtensions: false));
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
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
                foreach (var (key, value) in mapping.Entries.OrderBy(e => e.Key, StringComparer.Ordinal))
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

    /// <summary>RFC 4648 base32, lowercase, unpadded.</summary>
    private static class Base32
    {
        private const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

        public static string Encode(ReadOnlySpan<byte> data)
        {
            var builder = new StringBuilder((data.Length * 8 + 4) / 5);
            int buffer = 0, bits = 0;
            foreach (var b in data)
            {
                buffer = (buffer << 8) | b;
                bits += 8;
                while (bits >= 5)
                {
                    builder.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                    bits -= 5;
                }
            }

            if (bits > 0)
            {
                builder.Append(Alphabet[(buffer << (5 - bits)) & 31]);
            }

            return builder.ToString();
        }
    }
}
