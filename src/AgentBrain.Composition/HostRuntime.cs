using AgentBrain.Application.Abstractions.Content;
using AgentBrain.Application.Abstractions.Storage;
using AgentBrain.Application.Authorization;
using AgentBrain.Application.Capture;
using AgentBrain.Application.Common;
using AgentBrain.Application.Configuration;
using AgentBrain.Application.Indexing;
using AgentBrain.Application.Operations;
using AgentBrain.Application.Operations.Content;
using AgentBrain.Application.Operations.Discovery;
using AgentBrain.Application.Operations.Index;
using AgentBrain.Application.Operations.Maintenance;
using AgentBrain.Application.Operations.Proposals;
using AgentBrain.Application.Operations.Publishing;
using AgentBrain.Application.Operations.Relationships;
using AgentBrain.Application.Retrieval;
using AgentBrain.Application.Writing;
using AgentBrain.Domain.Access;
using AgentBrain.Domain.Entries;
using AgentBrain.Infrastructure.Local.Configuration;
using AgentBrain.Infrastructure.Local.Locking;
using AgentBrain.Infrastructure.Local.Records;
using AgentBrain.Infrastructure.Markdown.Collections;
using AgentBrain.Infrastructure.Markdown.Content;
using AgentBrain.Infrastructure.Sqlite;

namespace AgentBrain.Composition;

/// <summary>
/// The server's composition root (plan §9.6): hosted collections (SQLite or Markdown) under one data directory,
/// one search index, and the same application operations the CLI uses. A remote search ends here; the server
/// never forwards to another server (spec §8.1).
/// </summary>
public sealed class HostRuntime : IDisposable
{
    /// <summary>Data layout under the data directory (docs/deployment.md).</summary>
    public static class Paths
    {
        public static string Database(string data) => Path.Combine(data, "db", "brain.db");

        public static string Index(string data) => Path.Combine(data, "cache", "index.db");

        public static string Backups(string data) => Path.Combine(data, "backups");

        public static string State(string data) => Path.Combine(data, "state");

        public static string Proposals(string data) => Path.Combine(data, "proposals");

        public static string Audit(string data) => Path.Combine(data, "audit");
    }

    private HostRuntime() { }

    public required HostConfiguration Host { get; init; }

    public required HostDatabase Database { get; init; }

    public required SqliteSearchIndex SearchIndex { get; init; }

    public required ApiKeyService Keys { get; init; }

    public required LocalKnowledgeProvider Provider { get; init; }

    public required IndexSynchronizer Synchronizer { get; init; }

    public required CollectionCatalog Catalog { get; init; }

    public required SearchOperation Search { get; init; }

    public required GetOperation Get { get; init; }

    public required RelationsOperation Relations { get; init; }

    public required MaintenanceOperation Maintenance { get; init; }

    public required ProposalOperation Proposals { get; init; }

    public required PublishOperation Publishing { get; init; }

    public required IndexOperation Index { get; init; }

    /// <summary>Directories of hosted Markdown collections, for file watching.</summary>
    public required IReadOnlyList<string> MarkdownRoots { get; init; }

    public void Dispose()
    {
        SearchIndex.Dispose();
        Database.Dispose();
    }

    public static Result<HostConfiguration> ReadConfiguration(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, HostConfigFile.FileName);
        return File.Exists(path)
            ? HostConfigFile.Parse(File.ReadAllText(path), path)
            : new Error(ErrorCode.ConfigInvalid, $"{path} does not exist. Run brain-server init first.");
    }

    public static Result<HostRuntime> Open(string dataDirectory, HostConfiguration host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var data = Path.GetFullPath(dataDirectory);
        var time = TimeProvider.System;
        var database = HostDatabase.Open(Paths.Database(data), Paths.Backups(data));
        var locks = new FileWriteCoordinator(Path.Combine(Paths.State(data), "locks"), time);
        var analyzer = new MarkdownAnalyzer();
        var writable = new List<WritableCollection>();
        var local = new List<LocalCollection>();
        var scanners = new List<ICollectionScanner>();
        var markdownRoots = new List<string>();
        const Capability all = Capability.Read | Capability.Maintain | Capability.Propose | Capability.Publish;

        foreach (var hosted in host.Collections)
        {
            IEntryReader reader;
            IEntryWriter? writer;
            ICollectionScanner scanner;
            if (hosted.Storage == HostStorage.Markdown)
            {
                var root = Path.GetFullPath(hosted.Path!, data);
                var opened = MarkdownCollection.Open(root, analyzer, hosted.ReadOnly ? null : locks);
                if (!opened.IsSuccess)
                {
                    database.Dispose();
                    return opened.Error;
                }

                reader = opened.Value;
                writer = opened.Value.IsWritable ? opened.Value : null;
                scanner = opened.Value;
                markdownRoots.Add(root);
            }
            else
            {
                var descriptor = new CollectionDescriptor(hosted.Id, hosted.Title, hosted.ReadOnly, new CollectionDefaults(hosted.Defaults, hosted.Source), EntryLimits.Default, []);
                var problems = descriptor.Validate();
                if (problems.Count > 0)
                {
                    database.Dispose();
                    return new Error(ErrorCode.ConfigInvalid, $"Collection '{hosted.Id}': " + string.Join("; ", problems.Select(p => p.Message)));
                }

                var store = new SqliteEntryStore(database, descriptor, analyzer, hosted.ReadOnly);
                reader = store;
                writer = store.IsWritable ? store : null;
                scanner = store;
            }

            var capabilities = Capabilities.Effective(all, reader.Collection.ReadOnly, writer is not null);
            var info = new CollectionInfo(hosted.Id, hosted.Title, capabilities, reader.Collection.ReadOnly, LocalKnowledgeProvider.Id);
            writable.Add(new WritableCollection(info, reader, writer, hosted.ImmediatePublish));
            local.Add(new LocalCollection(reader, info));
            scanners.Add(scanner);
        }

        var configuration = new EffectiveConfiguration(
            null,
            [.. host.Collections.Select(c => new RegisteredCollection(c.Id, c.Path ?? "", all, c.ImmediatePublish))],
            [],
            null,
            [],
            new SearchSettings(Relaxation.Default, ConfigDefaults.MaxResults, ConfigDefaults.MaxTokens, ConfigDefaults.MaxBytes, ConfigDefaults.CandidatesPerSource,
                [EntryKind.Knowledge, EntryKind.Memory, EntryKind.Uncertainty], false),
            new GetSettings(ConfigDefaults.GetMaxBytes, ConfigDefaults.BatchTargets, ConfigDefaults.BatchBytes),
            [.. local.SelectMany(c => c.Reader.Collection.Aliases)],
            Paths.State(data),
            Paths.Proposals(data),
            new Dictionary<string, ConfigOrigin>());

        var index = SqliteSearchIndex.Open(Paths.Index(data));
        var synchronizer = new IndexSynchronizer(index, scanners, locks);
        var freshness = new IndexFreshness(synchronizer);
        var provider = new LocalKnowledgeProvider(index, analyzer, local, [], null, null);
        var coordinator = new RetrievalCoordinator([(provider, null)]);
        var catalog = new CollectionCatalog(writable);
        var preparer = new ChangePreparer(time);
        var audit = new JsonlAuditLog(Paths.Audit(data));

        return new HostRuntime
        {
            Host = host,
            Database = database,
            SearchIndex = index,
            Keys = new ApiKeyService(new SqliteApiKeyStore(database), time),
            Provider = provider,
            Synchronizer = synchronizer,
            Catalog = catalog,
            Search = new SearchOperation(configuration, coordinator, freshness),
            Get = new GetOperation(configuration, coordinator, freshness),
            Relations = new RelationsOperation(coordinator, freshness),
            Maintenance = new MaintenanceOperation(configuration, catalog, coordinator, preparer, new SimilarEntryFinder(index, []), freshness),
            Proposals = new ProposalOperation(catalog, preparer, new FileProposalStore(Paths.Proposals(data)), audit, freshness, time),
            Publishing = new PublishOperation(catalog, preparer, new FileBatchJournal(Path.Combine(Paths.State(data), "batches")), audit, freshness, time),
            Index = new IndexOperation(index, synchronizer),
            MarkdownRoots = markdownRoots,
        };
    }
}
