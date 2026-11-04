using AgentBrain.Application.Abstractions.Content;
using AgentBrain.Application.Abstractions.Support;
using AgentBrain.Infrastructure.Local.Logging;
using AgentBrain.Infrastructure.Local.Records;
using AgentBrain.Application.Capture;
using AgentBrain.Application.Operations.Maintenance;
using AgentBrain.Application.Operations.Proposals;
using AgentBrain.Application.Operations.Publishing;
using AgentBrain.Application.Writing;
using AgentBrain.Application.Common;
using AgentBrain.Application.Configuration;
using AgentBrain.Application.Indexing;
using AgentBrain.Application.Operations;
using AgentBrain.Application.Operations.Content;
using AgentBrain.Application.Operations.Discovery;
using AgentBrain.Application.Operations.Index;
using AgentBrain.Application.Operations.Relationships;
using AgentBrain.Application.Retrieval;
using AgentBrain.Domain.Access;
using AgentBrain.Infrastructure.Local.Configuration;
using AgentBrain.Infrastructure.Local.Locking;
using AgentBrain.Infrastructure.Markdown.Collections;
using AgentBrain.Infrastructure.Markdown.Content;
using AgentBrain.Infrastructure.Sqlite;

namespace AgentBrain.Composition;

/// <summary>How to open a runtime: where the caller is, explicit choices and per-call overrides.</summary>
public sealed record RuntimeOptions(string WorkingDirectory)
{
    public string? ProjectDirectory { get; init; }

    public ConfigDocument? Invocation { get; init; }

    /// <summary>Read-only Markdown roots registered for this invocation only (<c>--collection-root</c>).</summary>
    public IReadOnlyList<string> ExtraCollectionRoots { get; init; } = [];

    public HostEnvironment Environment { get; init; } = HostEnvironment.Current;

    public IndexTokenizer Tokenizer { get; init; } = IndexTokenizer.Porter;

    /// <summary>Overrides the index location (tests and the evaluation runner).</summary>
    public string? IndexPath { get; init; }

    /// <summary>
    /// Extra read-only sources, each with its own in-memory index, searched alongside the configured ones. Used by
    /// the evaluation runner to exercise federation (fusion, duplicates, revision conflicts) without a server.
    /// </summary>
    public IReadOnlyList<(string SourceId, IReadOnlyList<string> Roots)> AdditionalSources { get; init; } = [];
}

/// <summary>
/// The composition root shared by every host (plan §2.2): CLI, MCP and the evaluation runner wire
/// configuration, storage, index and operations identically through this type.
/// </summary>
public sealed class BrainRuntime : IDisposable
{
    private readonly SqliteSearchIndex? _index;
    private readonly IReadOnlyList<IDisposable> _resources;

    private BrainRuntime(RuntimeParts parts)
    {
        Configuration = parts.Configuration;
        Diagnostics = parts.Diagnostics;
        _index = parts.Index;
        _resources = parts.Resources;
        var time = TimeProvider.System;
        var freshness = new IndexFreshness(parts.Synchronizer);
        var preparer = new ChangePreparer(time);
        Search = new SearchOperation(parts.Configuration, parts.Coordinator, freshness, parts.QueryLog);
        List = new ListOperation(parts.Coordinator, freshness);
        Get = new GetOperation(parts.Configuration, parts.Coordinator, freshness);
        Relations = new RelationsOperation(parts.Coordinator, freshness);
        Index = new IndexOperation(parts.Index, parts.Synchronizer);
        Maintenance = new MaintenanceOperation(parts.Configuration, parts.Catalog, parts.Coordinator, preparer,
            new SimilarEntryFinder(parts.Index, parts.Configuration.Aliases), freshness);
        Proposals = new ProposalOperation(parts.Catalog, preparer, new FileProposalStore(parts.ProposalsDirectory), parts.Audit, freshness, time);
        Publishing = new PublishOperation(parts.Catalog, preparer, new FileBatchJournal(Path.Combine(parts.StateDirectory, "batches")), parts.Audit, freshness, time);
        Catalog = parts.Catalog;
    }

    public EffectiveConfiguration Configuration { get; }

    /// <summary>Configuration warnings and collections that could not be opened. Hosts print these to stderr.</summary>
    public IReadOnlyList<ConfigDiagnostic> Diagnostics { get; }

    public SearchOperation Search { get; }

    public ListOperation List { get; }

    public GetOperation Get { get; }

    public RelationsOperation Relations { get; }

    public IndexOperation Index { get; }

    public MaintenanceOperation Maintenance { get; }

    public ProposalOperation Proposals { get; }

    public PublishOperation Publishing { get; }

    public CollectionCatalog Catalog { get; }

    public void Dispose()
    {
        _index?.Dispose();
        foreach (var resource in _resources)
        {
            resource.Dispose();
        }
    }

    /// <summary>Loads and layers configuration without opening anything (for <c>brain config</c>).</summary>
    public static async ValueTask<Result<ConfigResolution>> ResolveConfigurationAsync(RuntimeOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var invocation = WithExtraRoots(options, out var rootErrors);
        if (rootErrors.Count > 0)
        {
            return new Error(ErrorCode.ConfigInvalid, string.Join(" ", rootErrors));
        }

        var source = new ConfigFileSource(options.Environment);
        var input = await source.LoadAsync(new ConfigurationRequest(options.WorkingDirectory, options.ProjectDirectory, invocation), cancellationToken);
        return input.IsSuccess ? ConfigLayering.Resolve(input.Value) : input.Error;
    }

    public static async ValueTask<Result<BrainRuntime>> OpenAsync(RuntimeOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var resolution = await ResolveConfigurationAsync(options, cancellationToken);
        if (!resolution.IsSuccess)
        {
            return resolution.Error;
        }

        if (resolution.Value.HasErrors)
        {
            return new Error(ErrorCode.ConfigInvalid, "Configuration has errors: " + string.Join("; ", resolution.Value.Diagnostics.Where(d => d.Severity == Domain.Validation.IssueSeverity.Error)));
        }

        var configuration = resolution.Value.Configuration;
        var diagnostics = new List<ConfigDiagnostic>(resolution.Value.Diagnostics);
        var analyzer = new MarkdownAnalyzer();
        var collections = new List<LocalCollection>();
        var scanners = new List<ICollectionScanner>();
        var writable = new List<WritableCollection>();
        var dataDirectory = configuration.RuntimeDirectory ?? Path.Combine(ConfigLocations.UserDirectory(options.Environment), "local");
        var stateDirectory = Path.Combine(dataDirectory, "state");
        var locks = new FileWriteCoordinator(Path.Combine(stateDirectory, "locks"), TimeProvider.System);

        foreach (var registration in configuration.Collections)
        {
            var opened = MarkdownCollection.Open(registration.Path, analyzer, locks);
            if (!opened.IsSuccess)
            {
                diagnostics.Add(Warning($"collections.{registration.Id}", opened.Error.Message));
                continue;
            }

            var collection = opened.Value;
            if (collection.Collection.Id != registration.Id)
            {
                diagnostics.Add(Warning($"collections.{registration.Id}", $"The collection at '{registration.Path}' declares ID '{collection.Collection.Id}'; register it under that ID."));
                continue;
            }

            var capabilities = Capabilities.Effective(registration.Capabilities, collection.Collection.ReadOnly, collection.IsWritable);
            var info = new CollectionInfo(registration.Id, collection.Collection.Title, capabilities, collection.Collection.ReadOnly, LocalKnowledgeProvider.Id);
            collections.Add(new LocalCollection(collection, info));
            writable.Add(new WritableCollection(info, collection, collection.IsWritable ? collection : null, registration.ImmediatePublish));
            scanners.Add(collection);
        }

        SqliteSearchIndex? index = null;
        IndexSynchronizer? synchronizer = null;
        var sources = new List<(Application.Abstractions.Providers.IKnowledgeProvider, TimeSpan?)>();
        if (collections.Count > 0)
        {
            index = SqliteSearchIndex.Open(options.IndexPath ?? Path.Combine(dataDirectory, "cache", "index.db"), options.Tokenizer);
            synchronizer = new IndexSynchronizer(index, scanners, locks);
            var provider = new LocalKnowledgeProvider(index, analyzer, collections, configuration.Aliases, configuration.Project?.Id, configuration.Project?.RootDirectory);
            sources.Add((provider, null));
        }

        var resources = new List<IDisposable>();
        sources.AddRange(RemoteSources.Create(configuration, options.Environment, resources, diagnostics));
        foreach (var (sourceId, roots) in options.AdditionalSources)
        {
            var extra = roots.Select(r => MarkdownCollection.Open(r, analyzer)).Where(r => r.IsSuccess).Select(r => r.Value).ToList();
            var extraIndex = SqliteSearchIndex.Open(":memory:", options.Tokenizer);
            resources.Add(extraIndex);
            await new IndexSynchronizer(extraIndex, extra, locks).SynchronizeAsync(SyncMode.Rebuild, IndexSynchronizer.MaintenanceLockWait, cancellationToken);
            var extraCollections = extra.Select(c => new LocalCollection(c, new CollectionInfo(c.Collection.Id, c.Collection.Title, Capability.Read, true, sourceId))).ToList();
            sources.Add((new LocalKnowledgeProvider(extraIndex, analyzer, extraCollections, configuration.Aliases, configuration.Project?.Id, null, sourceId), null));
        }

        IQueryLog? queryLog = configuration.Search.Log ? new JsonlQueryLog(Path.Combine(stateDirectory, "query-log.jsonl")) : null;
        var proposals = configuration.ProposalsDirectory ?? Path.Combine(ConfigLocations.UserDirectory(options.Environment), "proposals");
        return new BrainRuntime(new RuntimeParts(
            configuration, diagnostics, index, new RetrievalCoordinator(sources, configuration.SourcePreference), synchronizer, queryLog,
            new CollectionCatalog(writable), new JsonlAuditLog(Path.Combine(stateDirectory, "audit")), proposals, stateDirectory, resources));
    }

    private sealed record RuntimeParts(
        EffectiveConfiguration Configuration,
        IReadOnlyList<ConfigDiagnostic> Diagnostics,
        SqliteSearchIndex? Index,
        RetrievalCoordinator Coordinator,
        IndexSynchronizer? Synchronizer,
        IQueryLog? QueryLog,
        CollectionCatalog Catalog,
        IAuditLog Audit,
        string ProposalsDirectory,
        string StateDirectory,
        IReadOnlyList<IDisposable> Resources);

    /// <summary>Registers <c>--collection-root</c> directories as read-only collections in the per-call document, keyed by their own ID.</summary>
    private static ConfigDocument? WithExtraRoots(RuntimeOptions options, out List<string> errors)
    {
        errors = [];
        if (options.ExtraCollectionRoots.Count == 0)
        {
            return options.Invocation;
        }

        var invocation = options.Invocation ?? new ConfigDocument();
        var collections = new Dictionary<string, CollectionRegistration>(invocation.Collections ?? new Dictionary<string, CollectionRegistration>(), StringComparer.Ordinal);
        foreach (var root in options.ExtraCollectionRoots)
        {
            var path = Path.GetFullPath(root, options.WorkingDirectory);
            var descriptorPath = Path.Combine(path, CollectionFileReader.FileName);
            if (!File.Exists(descriptorPath))
            {
                errors.Add($"'{path}' has no {CollectionFileReader.FileName}.");
                continue;
            }

            var descriptor = CollectionFileReader.Parse(File.ReadAllText(descriptorPath), descriptorPath);
            if (!descriptor.IsSuccess)
            {
                errors.Add(descriptor.Error.Message);
                continue;
            }

            collections[descriptor.Value.Id.Value] = new CollectionRegistration(path, ["read"]);
        }

        return invocation with { Collections = collections };
    }

    private static ConfigDiagnostic Warning(string path, string message) =>
        new(Domain.Validation.IssueSeverity.Warning, path, message, null);
}
