using AgentBrain.Application.Abstractions.Content;
using AgentBrain.Application.Abstractions.Indexing;
using AgentBrain.Application.Abstractions.Storage;
using AgentBrain.Application.Retrieval;
using AgentBrain.Application.Retrieval.QueryAnalysis;
using AgentBrain.Domain.Entries;
using AgentBrain.Domain.Identity;
using AgentBrain.Domain.Revisions;

namespace AgentBrain.Infrastructure.Sqlite.Tests;

public sealed class SqliteSearchIndexTests : IDisposable
{
    private static readonly CollectionId Kb = CollectionId.Parse("kb");
    private static readonly CollectionId Game = CollectionId.Parse("game");
    private readonly SqliteSearchIndex _index = SqliteSearchIndex.Open(":memory:");

    public void Dispose() => _index.Dispose();

    private static ScannedFile File(
        CollectionId collection,
        string id,
        string title,
        string body = "",
        string? path = null,
        EntryKind kind = EntryKind.Knowledge,
        Publication publication = Publication.Published,
        DateOnly? archived = null,
        Resolution? resolution = null,
        bool blocking = false,
        string[]? tags = null,
        string[]? symbols = null,
        string[]? files = null,
        PackageConstraint[]? packages = null,
        string[]? projects = null,
        ContentLink[]? links = null)
    {
        var metadata = new EntryMetadata
        {
            Id = EntryId.Parse(id),
            Kind = kind,
            Title = title,
            Summary = title + " summary",
            Tags = tags ?? [],
            Symbols = symbols ?? [],
            Files = files ?? [],
            Publication = publication,
            Archived = archived,
            Resolution = resolution,
            Blocking = blocking ? true : null,
        };
        var applicability = new Applicability(
            projects is null ? ApplicabilityScope.General : ApplicabilityScope.Project, projects ?? [], [], packages ?? [], []);
        var stamp = new FileStamp(path ?? id + ".md", body.Length, 1);
        var entry = new StoredEntry(collection, metadata, applicability, body, RevisionCalculator.Compute(metadata, body), stamp.Path);
        return new ScannedFile(stamp, "hash-" + id + body.Length, entry, new ContentAnalysis([], links ?? []), []);
    }

    private ValueTask Apply(CollectionId collection, params ScannedFile[] files) =>
        _index.ApplyAsync(new IndexChanges(collection, files, []), TestContext.Current.CancellationToken);

    private async Task<List<string>> Find(string query, QueryContext? context = null, SearchFilters? filters = null, InclusionOptions? include = null, IReadOnlyList<EntryKind>? kinds = null)
    {
        Assert.True(QueryAnalyzer.TryAnalyze(query, [], out var terms, out _));
        var set = await _index.FindCandidatesAsync(
            new CandidateQuery(terms, context ?? QueryContext.Empty, [Kb, Game], kinds ?? [], filters ?? SearchFilters.None, include ?? InclusionOptions.Default, 50),
            TestContext.Current.CancellationToken);
        return [.. set.Candidates.Select(c => c.Entry.Ref.ToString())];
    }

    [Fact]
    public async Task Lexical_candidates_come_back_with_concept_hits()
    {
        await Apply(Kb, File(Kb, "retry", "Retry with backoff", "Use jitter."), File(Kb, "logging", "Structured logging"));
        Assert.True(QueryAnalyzer.TryAnalyze("retrying jitter", [], out var terms, out _));

        var set = await _index.FindCandidatesAsync(new CandidateQuery(terms, QueryContext.Empty, [Kb], [], SearchFilters.None, InclusionOptions.Default, 10), TestContext.Current.CancellationToken);

        var candidate = Assert.Single(set.Candidates);
        Assert.Equal("kb/retry", candidate.Entry.Ref.ToString());
        Assert.Equal(1, candidate.LexicalRank);
        Assert.Contains(candidate.Hits, h => h is { ConceptIndex: 0, Descriptive: true });
        Assert.Contains(candidate.Hits, h => h is { ConceptIndex: 1, Descriptive: false });
        Assert.Equal([1, 1], set.ConceptDocumentFrequency);
        Assert.Equal(2, set.TotalEntries);
    }

    [Theory]
    [InlineData("\"retry")]
    [InlineData("retry\" OR \"x")]
    [InlineData("title: retry")]
    [InlineData("retry NEAR logging")]
    [InlineData("retr* -logging")]
    [InlineData("{title}: retry ^ AND NOT")]
    public async Task Fts_syntax_in_queries_is_inert(string query)
    {
        await Apply(Kb, File(Kb, "retry", "Retry with backoff"));
        var found = await Find(query);
        Assert.True(found.Count <= 1);
    }

    [Fact]
    public async Task Default_eligibility_hides_drafts_history_and_resolved_uncertainties()
    {
        await Apply(Kb,
            File(Kb, "live", "Cache rules"),
            File(Kb, "draft", "Cache draft", publication: Publication.Draft),
            File(Kb, "old", "Cache archived", archived: new DateOnly(2026, 1, 1)),
            File(Kb, "answered", "Cache question", kind: EntryKind.Uncertainty, resolution: Resolution.Resolved));

        Assert.Equal(["kb/live"], await Find("cache"));
        Assert.Equal(4, (await Find("cache", include: new InclusionOptions(History: true, Drafts: true))).Count);
    }

    [Fact]
    public async Task Kind_and_tag_filters_restrict_candidates()
    {
        await Apply(Kb,
            File(Kb, "a", "Cache a", kind: EntryKind.Memory, tags: ["perf", "web"]),
            File(Kb, "b", "Cache b", kind: EntryKind.Knowledge, tags: ["perf"]),
            File(Kb, "c", "Cache c", kind: EntryKind.Report, tags: ["web"]));

        Assert.Equal(["kb/a", "kb/b"], (await Find("cache", kinds: [EntryKind.Memory, EntryKind.Knowledge])).Order());
        Assert.Equal(["kb/a"], await Find("cache", filters: new SearchFilters(Tags: ["perf", "web"])));
        Assert.Equal(3, (await Find("cache", filters: new SearchFilters(Tags: ["perf", "web"], TagMatch: TagMatch.Any))).Count);
        Assert.Equal(["kb/b"], await Find("cache", filters: new SearchFilters(Kinds: [EntryKind.Knowledge])));
    }

    [Fact]
    public async Task Incompatible_versions_are_excluded_unless_requested_and_unknown_is_kept()
    {
        await Apply(Kb,
            File(Kb, "new-api", "Entities baking", packages: [new("com.unity.entities", ">=1.0")]),
            File(Kb, "old-api", "Entities conversion", packages: [new("com.unity.entities", "<1.0")]));
        var context = new QueryContext(Packages: new Dictionary<string, string> { ["com.unity.entities"] = "1.2.4" });

        Assert.Equal(["kb/new-api"], await Find("entities", context));
        Assert.Equal(2, (await Find("entities")).Count);

        Assert.True(QueryAnalyzer.TryAnalyze("entities", [], out var terms, out _));
        var included = await _index.FindCandidatesAsync(
            new CandidateQuery(terms, context, [Kb], [], SearchFilters.None, new InclusionOptions(Incompatible: true), 10), TestContext.Current.CancellationToken);
        Assert.True(included.Candidates.Single(c => c.Entry.Ref.Entry.Value == "old-api").Incompatible);
    }

    [Fact]
    public async Task Structural_candidates_come_from_symbols_files_packages_and_blocking_questions()
    {
        await Apply(Game,
            File(Game, "by-symbol", "Alpha", symbols: ["SaveSystem"], projects: ["space-game"]),
            File(Game, "by-file", "Beta", files: ["Assets/Save/**"], projects: ["space-game"]),
            File(Game, "question", "Gamma", kind: EntryKind.Uncertainty, resolution: Resolution.Unresolved, blocking: true, projects: ["space-game"]),
            File(Game, "other-question", "Delta", kind: EntryKind.Uncertainty, resolution: Resolution.Unresolved, blocking: true, projects: ["web-shop"]));
        var context = new QueryContext("space-game", Files: ["Assets/Save/Slot.cs"], Symbols: ["savesystem"]);

        var found = await Find("nothing matches this", context);

        Assert.Equal(["game/by-file", "game/by-symbol", "game/question"], found.Order());
    }

    [Fact]
    public async Task Duplicate_ids_keep_the_first_path_and_report_the_rest()
    {
        await Apply(Kb, File(Kb, "same", "Cache one", path: "a/same.md"), File(Kb, "same", "Cache two", path: "b/same.md"));

        var found = await _index.FindAsync(Kb, EntryId.Parse("same"), TestContext.Current.CancellationToken);
        Assert.Equal("a/same.md", found!.Path);
        var diagnostics = await _index.DiagnosticsAsync(TestContext.Current.CancellationToken);
        Assert.Equal("b/same.md", Assert.Single(diagnostics, d => d.Issue.Code == "duplicate_id").Path);

        await _index.ApplyAsync(new IndexChanges(Kb, [], ["a/same.md"]), TestContext.Current.CancellationToken);
        Assert.Equal("b/same.md", (await _index.FindAsync(Kb, EntryId.Parse("same"), TestContext.Current.CancellationToken))!.Path);
        Assert.DoesNotContain(await _index.DiagnosticsAsync(TestContext.Current.CancellationToken), d => d.Issue.Code == "duplicate_id");
    }

    [Fact]
    public async Task Links_are_extracted_and_backlinks_page_in_stable_order()
    {
        var link = new ContentLink("entry://kb/target#usage", "the target");
        await Apply(Kb,
            File(Kb, "target", "Target"),
            File(Kb, "a", "A", links: [link]),
            File(Kb, "b", "B", links: [link, new ContentLink("https://example.com", "web"), new ContentLink("entry://bad link", "broken")]),
            File(Kb, "c", "C", links: [link]));

        var outgoing = await _index.OutgoingLinksAsync(EntryRef.Parse("kb/b"), TestContext.Current.CancellationToken);
        Assert.Equal("kb/target#usage", Assert.Single(outgoing).Target.ToString());
        Assert.Contains(await _index.DiagnosticsAsync(TestContext.Current.CancellationToken), d => d.Issue.Code == "invalid_reference" && d.Path == "b.md");

        var first = await _index.BacklinksAsync(EntryRef.Parse("kb/target"), null, 2, TestContext.Current.CancellationToken);
        Assert.Equal(["kb/a", "kb/b"], first.Items.Select(l => l.Source.ToString()));
        var second = await _index.BacklinksAsync(EntryRef.Parse("kb/target"), EntryRef.Parse(first.NextCursor!), 2, TestContext.Current.CancellationToken);
        Assert.Equal(["kb/c"], second.Items.Select(l => l.Source.ToString()));
        Assert.Null(second.NextCursor);
    }

    [Fact]
    public async Task Listing_enumerates_everything_page_by_page()
    {
        await Apply(Kb, [.. Enumerable.Range(0, 7).Select(i => File(Kb, $"entry-{i}", $"Entry {i}"))]);
        var seen = new List<string>();
        EntryRef? after = null;
        do
        {
            var page = await _index.ListAsync(new IndexListQuery([Kb], null, SearchFilters.None, InclusionOptions.Default, false, after, 3), TestContext.Current.CancellationToken);
            seen.AddRange(page.Items.Select(i => i.Ref.Entry.Value));
            after = page.NextCursor is null ? null : EntryRef.Parse(page.NextCursor);
        }
        while (after is not null);

        Assert.Equal(Enumerable.Range(0, 7).Select(i => $"entry-{i}"), seen);
    }

    [Fact]
    public async Task Stamps_track_files_and_removed_collections_are_dropped()
    {
        await Apply(Kb, File(Kb, "a", "A"));
        await Apply(Game, File(Game, "b", "B"));
        Assert.Contains("a.md", (await _index.GetStampsAsync(Kb, TestContext.Current.CancellationToken)).Keys);

        await _index.RetainCollectionsAsync([Game], TestContext.Current.CancellationToken);

        Assert.Empty(await _index.GetStampsAsync(Kb, TestContext.Current.CancellationToken));
        Assert.Equal(1, (await _index.GetStatusAsync(TestContext.Current.CancellationToken)).Entries);
    }

    [Fact]
    public async Task Reset_discards_everything()
    {
        await Apply(Kb, File(Kb, "a", "A"));
        await _index.ResetAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, (await _index.GetStatusAsync(TestContext.Current.CancellationToken)).Entries);
    }

    [Fact]
    public async Task A_tokenizer_change_rebuilds_the_index()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("brain-index-").FullName, "index.db");
        using (var porter = SqliteSearchIndex.Open(path))
        {
            await porter.ApplyAsync(new IndexChanges(Kb, [File(Kb, "a", "A")], []), TestContext.Current.CancellationToken);
        }

        using var plain = SqliteSearchIndex.Open(path, IndexTokenizer.Unicode61);
        Assert.Equal(0, (await plain.GetStatusAsync(TestContext.Current.CancellationToken)).Entries);
        Assert.Contains("unicode61", (await plain.GetStatusAsync(TestContext.Current.CancellationToken)).Tokenizer, StringComparison.Ordinal);
    }
}
