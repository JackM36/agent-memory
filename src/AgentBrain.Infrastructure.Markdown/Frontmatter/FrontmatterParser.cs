using AgentBrain.Domain.Metadata;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace AgentBrain.Infrastructure.Markdown.Frontmatter;

/// <summary>A Markdown file split into its frontmatter tree and body. <see cref="BodyLine"/> is the 1-based line the body starts on.</summary>
public sealed record FrontmatterDocument(MetadataMapping Metadata, string Body, int BodyLine);

/// <summary>Why a file could not be split or parsed. Reported as a per-file diagnostic; other files still index.</summary>
public sealed record FrontmatterError(string Message, int? Line);

/// <summary>
/// Splits <c>---</c>-delimited YAML frontmatter from a Markdown body and converts it to the
/// format-neutral metadata tree. Uses the YamlDotNet representation model only (no reflection; AOT-safe).
/// </summary>
public static class FrontmatterParser
{
    private const string Fence = "---";

    public static (FrontmatterDocument? Document, FrontmatterError? Error) Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var content = text.TrimStart('﻿').ReplaceLineEndings("\n");
        if (!content.StartsWith(Fence + "\n", StringComparison.Ordinal))
        {
            return (null, new FrontmatterError("The file does not start with '---' frontmatter.", 1));
        }

        var close = content.IndexOf("\n" + Fence + "\n", Fence.Length, StringComparison.Ordinal);
        var closeAtEnd = content.EndsWith("\n" + Fence, StringComparison.Ordinal) ? content.Length - Fence.Length - 1 : -1;
        if (close < 0)
        {
            close = closeAtEnd;
        }

        if (close < 0)
        {
            return (null, new FrontmatterError("The frontmatter is not closed with '---'.", 1));
        }

        var yaml = content[(Fence.Length + 1)..(close + 1)];
        var bodyStart = Math.Min(content.Length, close + Fence.Length + 2);
        var bodyLine = content.AsSpan(0, bodyStart).Count('\n') + 1;

        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException ex)
        {
            return (null, new FrontmatterError($"Invalid YAML: {ex.Message}", (int)ex.Start.Line + 1));
        }

        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            return (null, new FrontmatterError("Frontmatter must be a single set of fields.", 2));
        }

        return (new FrontmatterDocument((MetadataMapping)Convert(root), content[bodyStart..], bodyLine), null);
    }

    private static MetadataNode Convert(YamlNode node)
    {
        // Frontmatter starts on line 2 of the file (line 1 is the opening fence).
        var line = (int)node.Start.Line + 1;
        return node switch
        {
            YamlScalarNode { Style: ScalarStyle.Plain, Value: null or "" or "~" or "null" } => new MetadataNull(line),
            YamlScalarNode scalar => new MetadataScalar(scalar.Value ?? "", scalar.Style is not ScalarStyle.Plain, line),
            YamlSequenceNode sequence => new MetadataSequence([.. sequence.Children.Select(Convert)], line),
            YamlMappingNode mapping => new MetadataMapping(
                [.. mapping.Children.Select(kv => new KeyValuePair<string, MetadataNode>(((YamlScalarNode)kv.Key).Value ?? "", Convert(kv.Value)))],
                line),
            _ => new MetadataNull(line),
        };
    }
}
