namespace AgentBrain.Domain.Sections;

/// <summary>A heading found by a Markdown parser: level 1–6, its text, and the offset where its line starts in the body.</summary>
public sealed record Heading(int Level, string Text, int Start);

/// <summary>A retrievable section: from its heading to the next heading of the same or higher level.</summary>
public sealed record Section(string Anchor, int Level, string Title, int Start, int End)
{
    public int Length => End - Start;
}

/// <summary>
/// Section boundaries of one entry body (plan §5.13). Parsing (and skipping headings inside fenced
/// code) is the Markdown adapter's job; this type only applies the boundary and anchor rules.
/// </summary>
public sealed class SectionMap
{
    private readonly Dictionary<string, Section> _byAnchor;

    private SectionMap(IReadOnlyList<Section> sections)
    {
        Sections = sections;
        _byAnchor = sections.ToDictionary(s => s.Anchor, StringComparer.Ordinal);
    }

    public IReadOnlyList<Section> Sections { get; }

    public static SectionMap Build(string body, IReadOnlyList<Heading> headings)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(headings);

        var sections = new List<Section>(headings.Count + 1);
        var firstStart = headings.Count > 0 ? headings[0].Start : body.Length;
        if (!string.IsNullOrWhiteSpace(body[..firstStart]))
        {
            sections.Add(new Section(HeadingAnchor.Intro, 0, "", 0, firstStart));
        }

        var assigner = new AnchorAssigner();
        for (var i = 0; i < headings.Count; i++)
        {
            var heading = headings[i];
            var end = body.Length;
            for (var j = i + 1; j < headings.Count; j++)
            {
                if (headings[j].Level <= heading.Level)
                {
                    end = headings[j].Start;
                    break;
                }
            }

            sections.Add(new Section(assigner.Assign(heading.Text), heading.Level, heading.Text.Trim(), heading.Start, end));
        }

        return new SectionMap(sections);
    }

    public Section? Find(string anchor) => _byAnchor.GetValueOrDefault(anchor);
}
