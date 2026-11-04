using AgentBrain.Application.Abstractions.Providers;
using AgentBrain.Application.Abstractions.Storage;
using AgentBrain.Application.Common;
using AgentBrain.Application.Configuration;
using AgentBrain.Application.Indexing;
using AgentBrain.Application.Retrieval;
using AgentBrain.Composition;
using AgentBrain.Domain.Access;
using AgentBrain.Domain.Entries;
using AgentBrain.Domain.Identity;
using AgentBrain.Infrastructure.Http;
using AgentBrain.Infrastructure.Local.Locking;
using AgentBrain.Infrastructure.Markdown.Collections;
using AgentBrain.Infrastructure.Markdown.Content;
using AgentBrain.Infrastructure.Sqlite;
using AgentBrain.Server.Tests;

namespace AgentBrain.ProviderContract.Tests;

/// <summary>One storage-and-retrieval setup under test: a writer to seed content, a sync step and the provider.</summary>
public interface IContractFixture : IAsyncDisposable
{
    CollectionId Collection { get; }

    IEntryWriter Writer { get; }

    IKnowledgeProvider Provider { get; }

    ValueTask SyncAsync();
}

/// <summary>
/// The shared provider contract (plan §11): Markdown storage, SQLite storage and a remote host behave the same
/// for every retrieval and write scenario.
/// </summary>
public sealed class ProviderContractTests
{
    public static TheoryData<string> Adapters => ["markdown", "sqlite", "remote"];

    private static async Task<IContractFixture> CreateAsync(string adapter) => adapter switch
    {
        "markdown" => LocalFixture.Markdown(),
        "sqlite" => LocalFixture.Sqlite(),
        _ => await RemoteFixture.StartAsync(),
    };

    private static EntryMetadata Metadata(string id, string title, string summary, params string[] tags) => new()
    {
        Id = EntryId.Parse(id), Kind = EntryKind.Knowledge, Title = title, Summary = summary, Tags = tags,
    };

    private static SearchRequest Search(string query) =>
        new(query, QueryContext.Empty, SearchFilters.None, Relaxation.Default, new ResponseLimits(10, 4000), InclusionOptions.Default, 20);

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task Created_entries_are_searchable_and_fetchable_by_section(string adapter)
    {
        await using var f = await CreateAsync(adapter);
        await f.Writer.CreateAsync(Metadata("retry-policy", "Retry policy", "Retry idempotent calls with backoff."), "# Rules\n\nUse jitter.\n\n# Limits\n\nThree attempts.\n", CancellationToken.None);
        await f.Writer.CreateAsync(Metadata("logging", "Structured logging", "Log events with named fields."), "Body.\n", CancellationToken.None);
        await f.SyncAsync();

        var search = await f.Provider.SearchAsync(Search("retry backoff"), CancellationToken.None);
        Assert.True(search.IsSuccess, search.Error?.Message);
        Assert.Equal($"{f.Collection}/retry-policy", Assert.Single(search.Value.Results).Entry.Ref.ToString());

        var section = await f.Provider.GetAsync(new GetRequest(new EntryRef(f.Collection, EntryId.Parse("retry-policy"), "limits"), 4000, false), CancellationToken.None);
        Assert.True(section.IsSuccess, section.Error?.Message);
        Assert.Equal("# Limits\n\nThree attempts.\n", section.Value.Body);

        var missingSection = await f.Provider.GetAsync(new GetRequest(new EntryRef(f.Collection, EntryId.Parse("retry-policy"), "nope"), 4000, false), CancellationToken.None);
        Assert.Equal(ErrorCode.SectionNotFound, missingSection.Error!.Code);
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task Listing_pages_through_every_entry_in_order(string adapter)
    {
        await using var f = await CreateAsync(adapter);
        foreach (var i in Enumerable.Range(0, 7))
        {
            await f.Writer.CreateAsync(Metadata($"entry-{i}", $"Entry {i}", $"Summary {i}."), "Body.\n", CancellationToken.None);
        }

        await f.SyncAsync();
        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var page = await f.Provider.ListAsync(new ListRequest(SearchFilters.None, InclusionOptions.Default, false, cursor, 3), CancellationToken.None);
            Assert.True(page.IsSuccess, page.Error?.Message);
            seen.AddRange(page.Value.Items.Select(i => i.Entry.Ref.Entry.Value));
            cursor = page.Value.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal(Enumerable.Range(0, 7).Select(i => $"entry-{i}"), seen);
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task Stale_revisions_are_rejected_and_deletes_disappear(string adapter)
    {
        await using var f = await CreateAsync(adapter);
        var created = await f.Writer.CreateAsync(Metadata("cache", "Cache rules", "Invalidate by key prefix."), "Body.\n", CancellationToken.None);
        var updated = await f.Writer.ReplaceAsync(EntryId.Parse("cache"), created.Value, Metadata("cache", "Cache rules", "Invalidate by prefix."), "Body.\n", CancellationToken.None);
        Assert.True(updated.IsSuccess);

        var stale = await f.Writer.ReplaceAsync(EntryId.Parse("cache"), created.Value, Metadata("cache", "Cache rules", "Lost update."), "Body.\n", CancellationToken.None);
        Assert.Equal(ErrorCode.RevisionConflict, stale.Error!.Code);

        await f.SyncAsync();
        var current = await f.Provider.GetAsync(new GetRequest(new EntryRef(f.Collection, EntryId.Parse("cache")), 4000, false), CancellationToken.None);
        Assert.Equal("Invalidate by prefix.", current.Value.Entry.Summary);
        Assert.Equal(updated.Value, current.Value.Entry.Revision);

        Assert.True((await f.Writer.DeleteAsync(EntryId.Parse("cache"), updated.Value, CancellationToken.None)).IsSuccess);
        await f.SyncAsync();
        Assert.Empty((await f.Provider.SearchAsync(Search("cache rules"), CancellationToken.None)).Value.Results);
        Assert.Equal(ErrorCode.NotFound, (await f.Provider.GetAsync(new GetRequest(new EntryRef(f.Collection, EntryId.Parse("cache")), 4000, false), CancellationToken.None)).Error!.Code);
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task References_and_backlinks_resolve_by_identity(string adapter)
    {
        await using var f = await CreateAsync(adapter);
        await f.Writer.CreateAsync(Metadata("target", "Target", "The linked entry."), "# Usage\n\nText.\n", CancellationToken.None);
        await f.Writer.CreateAsync(Metadata("source", "Source", "Links to the target."),
            $"See [the target](entry://{f.Collection}/target#usage) and [nothing](entry://{f.Collection}/missing).\n", CancellationToken.None);
        await f.SyncAsync();

        var references = await f.Provider.ReferencesAsync(new EntryRef(f.Collection, EntryId.Parse("source")), CancellationToken.None);
        Assert.True(references.IsSuccess, references.Error?.Message);
        Assert.Null(references.Value.Single(r => r.Target.Entry.Value == "target").Problem);
        Assert.Equal(ReferenceProblem.TargetMissing, references.Value.Single(r => r.Target.Entry.Value == "missing").Problem);

        var backlinks = await f.Provider.BacklinksAsync(new EntryRef(f.Collection, EntryId.Parse("target")), null, 10, CancellationToken.None);
        Assert.Equal($"{f.Collection}/source", Assert.Single(backlinks.Value.Items).Source.ToString());
    }
}

internal sealed class LocalFixture : IContractFixture
{
    private readonly SqliteSearchIndex _index;
    private readonly IndexSynchronizer _synchronizer;
    private readonly Action _cleanup;

    private LocalFixture(CollectionId collection, IEntryWriter writer, IEntryReader reader, Application.Abstractions.Content.ICollectionScanner scanner, string locks, Action cleanup)
    {
        Collection = collection;
        Writer = writer;
        _cleanup = cleanup;
        _index = SqliteSearchIndex.Open(":memory:");
        _synchronizer = new IndexSynchronizer(_index, [scanner], new FileWriteCoordinator(locks, TimeProvider.System));
        var info = new CollectionInfo(collection, "KB", Capability.Read | Capability.Maintain, false, LocalKnowledgeProvider.Id);
        Provider = new LocalKnowledgeProvider(_index, new MarkdownAnalyzer(), [new LocalCollection(reader, info)], [], null, null);
    }

    public CollectionId Collection { get; }

    public IEntryWriter Writer { get; }

    public IKnowledgeProvider Provider { get; }

    public static LocalFixture Markdown()
    {
        var root = Directory.CreateTempSubdirectory("brain-contract-md-").FullName;
        File.WriteAllText(Path.Combine(root, "collection.json"), """{ "schema": 1, "id": "kb", "title": "KB", "defaults": { "applies": { "scope": "general" } } }""");
        var locks = new FileWriteCoordinator(Path.Combine(root, ".locks"), TimeProvider.System);
        var collection = MarkdownCollection.Open(root, new MarkdownAnalyzer(), locks).Value;
        return new LocalFixture(CollectionId.Parse("kb"), collection, collection, collection, Path.Combine(root, ".locks"), () => Directory.Delete(root, true));
    }

    public static LocalFixture Sqlite()
    {
        var database = HostDatabase.Open(":memory:", null);
        var descriptor = new CollectionDescriptor(CollectionId.Parse("store"), "Store", false, new CollectionDefaults(new ApplicabilityOverrides(ApplicabilityScope.General)), EntryLimits.Default, []);
        var store = new SqliteEntryStore(database, descriptor, new MarkdownAnalyzer());
        var locks = Directory.CreateTempSubdirectory("brain-contract-locks-").FullName;
        return new LocalFixture(descriptor.Id, store, store, store, locks, () =>
        {
            database.Dispose();
            Directory.Delete(locks, true);
        });
    }

    public async ValueTask SyncAsync() => await _synchronizer.SynchronizeAsync(SyncMode.Incremental, TimeSpan.FromSeconds(5), CancellationToken.None);

    public ValueTask DisposeAsync()
    {
        _index.Dispose();
        _cleanup();
        return ValueTask.CompletedTask;
    }
}

internal sealed class RemoteFixture : IContractFixture
{
    private readonly TestHost _host;
    private readonly RemoteHttpProvider _provider;

    private RemoteFixture(TestHost host, RemoteHttpProvider provider)
    {
        _host = host;
        _provider = provider;
    }

    public CollectionId Collection { get; } = CollectionId.Parse("team");

    public IEntryWriter Writer => _host.Runtime.Catalog.Find(Collection)!.Writer!;

    public IKnowledgeProvider Provider => _provider;

    public static async Task<RemoteFixture> StartAsync()
    {
        var host = await TestHost.StartAsync();
        var key = await host.KeyAsync("collection:team:read");
        var connection = new EffectiveConnection("remote", new Uri("http://localhost/"), new("KEY"), [CollectionId.Parse("team")], TimeSpan.FromSeconds(5), ForwardPolicy.Nothing);
        return new RemoteFixture(host, new RemoteHttpProvider(connection, key, null, host.Handler));
    }

    public ValueTask SyncAsync() => ValueTask.CompletedTask; // The server refreshes before every read.

    public async ValueTask DisposeAsync()
    {
        _provider.Dispose();
        await _host.DisposeAsync();
    }
}
