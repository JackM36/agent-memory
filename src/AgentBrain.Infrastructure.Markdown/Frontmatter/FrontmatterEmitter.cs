using System.Text;
using System.Text.RegularExpressions;
using AgentBrain.Domain.Metadata;

namespace AgentBrain.Infrastructure.Markdown.Frontmatter;

/// <summary>
/// Writes a metadata tree as YAML frontmatter in canonical field order (plan §5.4). Scalars that YAML could
/// misread are double-quoted; short lists of plain scalars use flow style (<c>[a, b]</c>). The body is
/// written unchanged after the closing fence, with line endings normalised to LF.
/// </summary>
public static partial class FrontmatterEmitter
{
    public static string Write(MetadataMapping metadata, string body)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(body);
        var text = new StringBuilder("---\n");
        WriteMapping(text, metadata, 0);
        text.Append("---\n");
        text.Append(body.ReplaceLineEndings("\n"));
        return text.ToString();
    }

    private static void WriteMapping(StringBuilder text, MetadataMapping mapping, int indent)
    {
        foreach (var (key, value) in mapping.Entries)
        {
            text.Append(' ', indent).Append(key).Append(':');
            WriteValue(text, value, indent);
        }
    }

    private static void WriteValue(StringBuilder text, MetadataNode value, int indent)
    {
        switch (value)
        {
            case MetadataScalar scalar:
                text.Append(' ').Append(Scalar(scalar.Value)).Append('\n');
                break;
            case MetadataNull:
                text.Append('\n');
                break;
            case MetadataSequence sequence when sequence.Items.All(i => i is MetadataScalar s && IsPlain(s.Value)) && FlowLength(sequence) <= 100:
                text.Append(" [").Append(string.Join(", ", sequence.Items.Cast<MetadataScalar>().Select(s => s.Value))).Append("]\n");
                break;
            case MetadataSequence sequence:
                text.Append('\n');
                foreach (var item in sequence.Items)
                {
                    text.Append(' ', indent + 2).Append('-');
                    if (item is MetadataMapping itemMapping)
                    {
                        var first = true;
                        foreach (var (key, nested) in itemMapping.Entries)
                        {
                            text.Append(first ? " " : new string(' ', indent + 4)).Append(key).Append(':');
                            WriteValue(text, nested, indent + 4);
                            first = false;
                        }
                    }
                    else
                    {
                        WriteValue(text, item, indent + 2);
                    }
                }

                break;
            case MetadataMapping nested:
                text.Append('\n');
                WriteMapping(text, nested, indent + 2);
                break;
        }
    }

    private static int FlowLength(MetadataSequence sequence) => sequence.Items.Cast<MetadataScalar>().Sum(s => s.Value.Length + 2);

    private static string Scalar(string value) => IsPlain(value) ? value : Quote(value);

    /// <summary>Plain only when YAML reads it back as the same string: no indicators, no ": " or " #", no leading/trailing space.</summary>
    private static bool IsPlain(string value) =>
        value.Length > 0 && value.Length <= 200 && value is not ("null" or "Null" or "NULL" or "~") && PlainScalar().IsMatch(value)
        && !value.Contains(": ", StringComparison.Ordinal) && !value.Contains(" #", StringComparison.Ordinal);

    private static string Quote(string value)
    {
        var text = new StringBuilder("\"");
        foreach (var c in value)
        {
            text.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ when char.IsControl(c) => $"\\u{(int)c:x4}",
                _ => c.ToString(),
            });
        }

        return text.Append('"').ToString();
    }

    [GeneratedRegex(@"^[A-Za-z0-9_./][A-Za-z0-9 _./+#@()=<>|-]*(?<! )$", RegexOptions.CultureInvariant)]
    private static partial Regex PlainScalar();
}
