using System.Text;
using AgentBrain.Application.Abstractions.Content;
using AgentBrain.Application.Abstractions.Indexing;
using AgentBrain.Application.Abstractions.Providers;
using AgentBrain.Application.Abstractions.Storage;
using AgentBrain.Application.Common;
using AgentBrain.Application.Decisions;
using AgentBrain.Application.Retrieval.QueryAnalysis;
using AgentBrain.Application.Retrieval.Ranking;
using AgentBrain.Domain.Entries;
using AgentBrain.Domain.Identity;
using AgentBrain.Domain.Sections;

namespace AgentBrain.Application.Retrieval;

/// <summary>A local collection as the provider sees it: storage, capabilities and where to show paths from.</summary>
public sealed record LocalCollection(IEntryReader Reader, CollectionInfo Info);

/// <summary>
/// Searches all local collections through one shared index, so their scores are directly comparable and
/// no fusion is needed between them (plan §6). Get reads authoritative storage, never the index copy.
/// </summary>
public sealed class LocalKnowledgeProvider : IKnowledgeProvider
{
    public const string Id = "local";

    private readonly ISearchIndex _index;
    private readonly IContentAnalyzer _analyzer;
    private readonly Dictionary<CollectionId, LocalCollection> _collections;
    private readonly IReadOnlyList<AliasGroup> _aliases;
    private readonly string? _projectId;
    private readonly string? _displayRoot;
    private readonly IRelevanceJudge _judge;

    public LocalKnowledgeProvider(
        ISearchIndex index,
        IContentAnalyzer analyzer,
        IReadOnlyList<LocalCollection> collections,
        IReadOnlyList<AliasGroup> aliases,
        string? projectId,
        string? displayRoot,
        string sourceId = Id,
        IRelevanceJudge? judge = null)
    {
        ArgumentNullException.ThrowIfNull(collections);
        _index = index;
        _analyzer = analyzer;
        _collections = collections.ToDictionary(c => c.Info.Id);
        _projectId = projectId;
        _displayRoot = displayRoot;
        _judge = judge ?? HeuristicRelevanceJudge.Instance;
        SourceId = sourceId;

        // Plan §5.7 scopes collection aliases to their collection's entries. Phase 2 applies them to the
        // whole query (ADR 0006); expansions only add equivalent terms, so the effect is limited to matching.
        _aliases = [.. aliases, .. collections.SelectMany(c => c.Reader.Collection.Aliases)];
        Collections = [.. collections.Select(c => c.Info).OrderBy(c => c.Id.Value, StringComparer.Ordinal)];
    }

    public string SourceId { get; }

    public IReadOnlyList<CollectionInfo> Collections { get; }

    public async ValueTask<Result<ProviderSearchResult>> SearchAsync(SearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!QueryAnalyzer.TryAnalyze(request.Query, _aliases, out var terms, out var problem))
        {
            return Error.Validation(problem!);
        }

        var query = new CandidateQuery(terms, request.Context, [.. _collections.Keys], request.Filters.Kinds ?? [], request.Filters, request.Include, request.CandidateLimit);
        var set = await _index.FindCandidatesAsync(query, cancellationToken);
        var judged = await _judge.JudgeAsync(new RelevanceInput(request.Query, set, terms, request.Context, request.Relaxation, request.Explain), cancellationToken);
        return new ProviderSearchResult(ToRanked(judged.Results), ToRanked(judged.Blocking), IndexStale: false, judged.Notes.Count == 0 ? null : judged.Notes);
    }

    public async ValueTask<Result<Page<RankedEntry>>> ListAsync(ListRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EntryRef? after = null;
        if (request.Cursor is not null && !EntryRef.TryParse(Cursors.Decode(request.Cursor), out after))
        {
            return Error.Validation("The cursor is not valid for this listing.");
        }

        var pageSize = Math.Clamp(request.PageSize, 1, PageDefaults.MaxPageSize);
        var page = await _index.ListAsync(
            new IndexListQuery([.. _collections.Keys], request.Filters.Kinds, request.Filters, request.Include, request.BlockingOnly, after, pageSize),
            cancellationToken);
        var items = page.Items.Select((e, i) => new RankedEntry(Summary(e), i + 1, Ranker.Classify(e.Applicability, _projectId), e.Ref.Collection.Value)).ToList();
        return new Page<RankedEntry>(items, page.NextCursor is null ? null : Cursors.Encode(page.NextCursor));
    }

    public async ValueTask<Result<EntryContent>> GetAsync(GetRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var target = request.Ref;
        if (!_collections.TryGetValue(target.Collection, out var collection))
        {
            return Error.NotFound($"Collection '{target.Collection}' is not configured.");
        }

        var indexed = await _index.FindAsync(target.Collection, target.Entry, cancellationToken);
        var stored = await collection.Reader.FindAsync(target.Entry, indexed?.Path, cancellationToken);
        if (stored is null)
        {
            return Error.NotFound($"'{target.WithoutAnchor()}' does not exist.");
        }

        var analysis = _analyzer.Analyze(stored.Body);
        var sections = SectionMap.Build(stored.Body, analysis.Headings);
        var body = stored.Body;
        if (target.Anchor is not null)
        {
            if (sections.Find(target.Anchor) is not { } section)
            {
                return RetrievalErrors.SectionNotFound(target);
            }

            body = stored.Body[section.Start..section.End];
        }

        var (shown, omitted, cut) = Truncate(body, request.MaxBytes);
        var offset = target.Anchor is null ? 0 : sections.Find(target.Anchor)!.Start;
        var remaining = omitted == 0
            ? []
            : sections.Sections.Where(s => s.Start >= offset + cut && s.End <= offset + body.Length).Select(s => s.Anchor).ToList();

        var references = request.WithReferences ? await ResolveReferencesAsync(target.WithoutAnchor(), cancellationToken) : null;
        var m = stored.Metadata;
        var summary = new EntrySummary(
            target.WithoutAnchor(), m.Kind, m.Title, m.Summary, m.Tags, stored.Applicability,
            m.Source ?? collection.Reader.Collection.Defaults.Source, Encoding.UTF8.GetByteCount(stored.Body), stored.Revision,
            DisplayPath(collection, stored.Locator));
        var lifecycle = new LifecycleInfo(
            m.Publication ?? collection.Reader.Collection.Defaults.Publication, m.Archived, m.SupersededBy, m.Resolution, m.IsBlockingUncertainty);
        return new EntryContent(summary, lifecycle, shown, omitted, remaining, references, target.Collection.Value);
    }

    public async ValueTask<Result<IReadOnlyList<OutgoingReference>>> ReferencesAsync(EntryRef entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (await _index.FindAsync(entry.Collection, entry.Entry, cancellationToken) is null)
        {
            return Error.NotFound($"'{entry.WithoutAnchor()}' does not exist.");
        }

        return Result<IReadOnlyList<OutgoingReference>>.Success(await ResolveReferencesAsync(entry.WithoutAnchor(), cancellationToken));
    }

    public async ValueTask<Result<Page<Backlink>>> BacklinksAsync(EntryRef entry, string? cursor, int pageSize, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        EntryRef? after = null;
        if (cursor is not null && !EntryRef.TryParse(Cursors.Decode(cursor), out after))
        {
            return Error.Validation("The cursor is not valid for this listing.");
        }

        var page = await _index.BacklinksAsync(entry.WithoutAnchor(), after, Math.Clamp(pageSize, 1, PageDefaults.MaxPageSize), cancellationToken);
        var items = page.Items.Select(l => new Backlink(l.Source, l.Target.Anchor, l.Text)).ToList();
        return new Page<Backlink>(items, page.NextCursor is null ? null : Cursors.Encode(page.NextCursor));
    }

    private async ValueTask<IReadOnlyList<OutgoingReference>> ResolveReferencesAsync(EntryRef source, CancellationToken cancellationToken)
    {
        var references = new List<OutgoingReference>();
        foreach (var link in await _index.OutgoingLinksAsync(source, cancellationToken))
        {
            ReferenceProblem? problem = null;
            if (!_collections.TryGetValue(link.Target.Collection, out var collection))
            {
                problem = ReferenceProblem.CollectionUnconfigured;
            }
            else if (await _index.FindAsync(link.Target.Collection, link.Target.Entry, cancellationToken) is not { } target)
            {
                problem = ReferenceProblem.TargetMissing;
            }
            else if (link.Target.Anchor is not null)
            {
                var stored = await collection.Reader.FindAsync(link.Target.Entry, target.Path, cancellationToken);
                var headings = stored is null ? [] : _analyzer.Analyze(stored.Body).Headings;
                if (stored is null || SectionMap.Build(stored.Body, headings).Find(link.Target.Anchor) is null)
                {
                    problem = ReferenceProblem.SectionMissing;
                }
            }

            references.Add(new OutgoingReference(link.Target, link.Text, problem));
        }

        return references;
    }

    private List<RankedEntry> ToRanked(IReadOnlyList<ScoredCandidate> scored) =>
    [
        .. scored.Select((s, i) => new RankedEntry(
            Summary(s.Candidate.Entry), i + 1, s.Applicability, SourceId == Id ? s.Candidate.Entry.Ref.Collection.Value : SourceId, s.Candidate.Incompatible, s.Explanation)),
    ];

    private EntrySummary Summary(IndexedEntry e)
    {
        var collection = _collections[e.Ref.Collection];
        var m = e.Metadata;
        return new EntrySummary(
            e.Ref, m.Kind, m.Title, m.Summary, m.Tags, e.Applicability,
            m.Source ?? collection.Reader.Collection.Defaults.Source, e.SizeBytes, e.Revision, DisplayPath(collection, e.Path));
    }

    /// <summary>The file path relative to the project root when the file is inside it, otherwise absolute.</summary>
    private string? DisplayPath(LocalCollection collection, string? locator)
    {
        if (locator is null)
        {
            return null;
        }

        var full = Path.GetFullPath(Path.Combine(collection.Reader.Location, locator));
        if (_displayRoot is not null)
        {
            var relative = Path.GetRelativePath(_displayRoot, full);
            if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
            {
                return relative.Replace('\\', '/');
            }
        }

        return full.Replace('\\', '/');
    }

    /// <summary>Cuts at the last line break within the byte limit so no line is split.</summary>
    private static (string Shown, int OmittedBytes, int CutIndex) Truncate(string body, int maxBytes)
    {
        var total = Encoding.UTF8.GetByteCount(body);
        if (total <= maxBytes)
        {
            return (body, 0, body.Length);
        }

        int bytes = 0, cut = 0;
        for (var i = 0; i < body.Length; i++)
        {
            bytes += Encoding.UTF8.GetByteCount(body.AsSpan(i, char.IsHighSurrogate(body[i]) && i + 1 < body.Length ? 2 : 1));
            if (char.IsHighSurrogate(body[i]))
            {
                i++;
            }

            if (bytes > maxBytes)
            {
                break;
            }

            if (body[i] == '\n')
            {
                cut = i + 1;
            }
        }

        return (body[..cut], total - Encoding.UTF8.GetByteCount(body.AsSpan(0, cut)), cut);
    }
}
