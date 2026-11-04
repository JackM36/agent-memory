using AgentBrain.Domain.Sections;

namespace AgentBrain.Domain.Tests;

public sealed class SectionTests
{
    [Theory]
    [InlineData("Managed References", "managed-references")]
    [InlineData("  When to use [SerializeReference]?  ", "when-to-use-serializereference")]
    [InlineData("snake_case & Kebab-Case", "snake_case--kebab-case")]
    [InlineData("Größe", "größe")]
    [InlineData("v2.0 — notes", "v20--notes")]
    [InlineData("???", "section")]
    public void Anchors_follow_the_github_convention(string heading, string expected) =>
        Assert.Equal(expected, HeadingAnchor.FromHeadingText(heading));

    [Fact]
    public void Duplicate_headings_get_numbered_suffixes_in_document_order()
    {
        var assigner = new AnchorAssigner();
        var anchors = new[] { "Notes", "Notes", "Notes-1", "Notes", "_intro" }.Select(assigner.Assign).ToList();
        Assert.Equal(["notes", "notes-1", "notes-1-1", "notes-2", "_intro-1"], anchors);
    }

    [Fact]
    public void Generated_anchors_are_valid_and_uppercase_is_not()
    {
        Assert.True(HeadingAnchor.IsValid("größe-1"));
        Assert.True(HeadingAnchor.IsValid(HeadingAnchor.Intro));
        Assert.False(HeadingAnchor.IsValid("Notes"));
        Assert.False(HeadingAnchor.IsValid("a b"));
        Assert.False(HeadingAnchor.IsValid(""));
    }

    [Fact]
    public void Sections_run_to_the_next_heading_of_the_same_or_higher_level()
    {
        const string body = "Intro text.\n# A\na\n## A1\na1\n## A2\na2\n# B\nb\n";
        var headings = new List<Heading>
        {
            new(1, "A", body.IndexOf("# A", StringComparison.Ordinal)),
            new(2, "A1", body.IndexOf("## A1", StringComparison.Ordinal)),
            new(2, "A2", body.IndexOf("## A2", StringComparison.Ordinal)),
            new(1, "B", body.IndexOf("# B", StringComparison.Ordinal)),
        };

        var map = SectionMap.Build(body, headings);

        Assert.Equal(["_intro", "a", "a1", "a2", "b"], map.Sections.Select(s => s.Anchor));
        Assert.Equal("Intro text.\n", Slice(body, map.Find("_intro")!));
        Assert.Equal("# A\na\n## A1\na1\n## A2\na2\n", Slice(body, map.Find("a")!));
        Assert.Equal("## A1\na1\n", Slice(body, map.Find("a1")!));
        Assert.Equal("# B\nb\n", Slice(body, map.Find("b")!));
        Assert.Null(map.Find("missing"));
    }

    [Fact]
    public void Whitespace_before_the_first_heading_is_not_an_intro_section()
    {
        var map = SectionMap.Build("\n\n# A\n", [new Heading(1, "A", 2)]);
        Assert.Equal(["a"], map.Sections.Select(s => s.Anchor));
    }

    private static string Slice(string body, Section section) => body[section.Start..section.End];
}
