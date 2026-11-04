using AgentBrain.Domain.Identity;
using AgentBrain.Domain.Sections;
using AgentBrain.Infrastructure.Markdown.Collections;
using AgentBrain.Infrastructure.Markdown.Content;

namespace AgentBrain.Infrastructure.Markdown.Tests;

public sealed class MarkdownCollectionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("brain-md-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static string Entry(string id, string body = "Body.\n") =>
        $"---\nschema: 1\nid: {id}\nkind: memory\ntitle: \"Title {id}\"\nsummary: \"Summary {id}\"\n---\n{body}";

    private MarkdownCollection Open()
    {
        Write("collection.json", """{ "schema": 1, "id": "kb", "title": "KB", "defaults": { "applies": { "scope": "general" } } }""");
        var result = MarkdownCollection.Open(_root, new MarkdownAnalyzer());
        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value;
    }

    [Fact]
    public void Analyzer_finds_headings_outside_code_and_entry_links()
    {
        const string body = "Intro [a](entry://kb/a#usage) and [web](https://x.y).\n\n# One\n\n```md\n# not a heading\n```\n\n## Two `code`\n![img](entry://kb/image)\n";

        var analysis = new MarkdownAnalyzer().Analyze(body);

        Assert.Equal([("One", 1), ("Two code", 2)], analysis.Headings.Select(h => (h.Text, h.Level)));
        Assert.Equal(body.IndexOf("# One", StringComparison.Ordinal), analysis.Headings[0].Start);
        Assert.Equal(["entry://kb/a#usage", "https://x.y"], analysis.Links.Select(l => l.Url));
        Assert.Equal("a", analysis.Links[0].Text);

        var sections = SectionMap.Build(body, analysis.Headings);
        Assert.Equal(["_intro", "one", "two-code"], sections.Sections.Select(s => s.Anchor));
    }

    [Fact]
    public void Scan_skips_the_readme_and_hidden_directories()
    {
        var collection = Open();
        Write("README.md", "# About this collection");
        Write("memory/a.md", Entry("a"));
        Write(".drafts/b.md", Entry("b"));
        Write("deep/nested/c.md", Entry("c"));

        Assert.Equal(["deep/nested/c.md", "memory/a.md"], collection.Scan().Select(s => s.Path));
    }

    [Fact]
    public void Load_reports_invalid_files_without_an_entry()
    {
        var collection = Open();
        Write("broken.md", "no frontmatter");
        Write("bad-field.md", Entry("bad-field").Replace("kind: memory", "kind: memory\nconfidence: high", StringComparison.Ordinal));
        Write("renamed.md", Entry("original-id"));

        var stamps = collection.Scan().ToDictionary(s => s.Path);
        Assert.Null(collection.Load(stamps["broken.md"]).Entry);
        Assert.Contains(collection.Load(stamps["bad-field.md"]).Issues, i => i.Code == "unknown_field");

        var renamed = collection.Load(stamps["renamed.md"]);
        Assert.Equal("original-id", renamed.Entry!.Metadata.Id.Value);
        Assert.Contains(renamed.Issues, i => i.Code == "file_name_mismatch");
        Assert.NotEmpty(renamed.Hash);
    }

    [Fact]
    public async Task Find_uses_the_hint_and_falls_back_after_a_move()
    {
        var collection = Open();
        Write("memory/moved.md", Entry("moved"));

        var viaHint = await collection.FindAsync(EntryId.Parse("moved"), "memory/moved.md", TestContext.Current.CancellationToken);
        Assert.Equal("memory/moved.md", viaHint!.Locator);

        File.Move(Path.Combine(_root, "memory", "moved.md"), Path.Combine(_root, "elsewhere.md"));
        var afterMove = await collection.FindAsync(EntryId.Parse("moved"), "memory/moved.md", TestContext.Current.CancellationToken);
        Assert.Equal("elsewhere.md", afterMove!.Locator);

        Assert.Null(await collection.FindAsync(EntryId.Parse("missing"), null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void A_directory_without_collection_json_is_not_a_collection()
    {
        var result = MarkdownCollection.Open(Path.Combine(_root, "nothing"), new MarkdownAnalyzer());
        Assert.False(result.IsSuccess);
        Assert.Contains("collection.json", result.Error.Message, StringComparison.Ordinal);
    }
}
