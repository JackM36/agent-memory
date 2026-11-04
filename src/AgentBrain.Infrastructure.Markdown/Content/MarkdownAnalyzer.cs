using System.Text;
using AgentBrain.Application.Abstractions.Content;
using AgentBrain.Domain.Sections;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace AgentBrain.Infrastructure.Markdown.Content;

/// <summary>
/// Extracts headings and links from an entry body with Markdig. Headings inside fenced code are not
/// headings to Markdig, so section boundaries ignore them (plan §5.13).
/// </summary>
public sealed class MarkdownAnalyzer : IContentAnalyzer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePreciseSourceLocation().Build();

    public ContentAnalysis Analyze(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var document = Markdig.Markdown.Parse(body, Pipeline);

        var headings = document.Descendants<HeadingBlock>()
            .Select(h => new Heading(h.Level, InlineText(h.Inline), LineStart(body, h.Span.Start)))
            .ToList();

        var links = document.Descendants<LinkInline>()
            .Where(l => !l.IsImage && !string.IsNullOrEmpty(l.Url))
            .Select(l => new ContentLink(l.Url!, InlineText(l)))
            .ToList();

        return new ContentAnalysis(headings, links);
    }

    private static int LineStart(string body, int offset)
    {
        var start = Math.Clamp(offset, 0, body.Length);
        while (start > 0 && body[start - 1] != '\n')
        {
            start--;
        }

        return start;
    }

    private static string InlineText(ContainerInline? container)
    {
        if (container is null)
        {
            return "";
        }

        var builder = new StringBuilder();
        Append(builder, container);
        return builder.ToString().Trim();
    }

    private static void Append(StringBuilder builder, Inline inline)
    {
        switch (inline)
        {
            case LiteralInline literal:
                builder.Append(literal.Content.ToString());
                break;
            case CodeInline code:
                builder.Append(code.Content);
                break;
            case LineBreakInline:
                builder.Append(' ');
                break;
            case ContainerInline container:
                foreach (var child in container)
                {
                    Append(builder, child);
                }

                break;
        }
    }
}
