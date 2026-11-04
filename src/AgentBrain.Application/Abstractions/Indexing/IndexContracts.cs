using AgentBrain.Application.Common;
using AgentBrain.Application.Retrieval;
using AgentBrain.Domain.Entries;
using AgentBrain.Domain.Identity;
using AgentBrain.Domain.Revisions;
using AgentBrain.Domain.Validation;

namespace AgentBrain.Application.Abstractions.Indexing;

/// <summary>Analysed query terms handed to the index (plan §5.7). Raw query text never reaches the index.</summary>
public sealed record QueryTerms(IReadOnlyList<QueryConcept> Concepts)
{
    public static readonly QueryTerms Empty = new([]);
}

/// <summary>
/// One query concept: the original term plus equivalent terms (alias expansions, identifier splits).
/// A multi-word term is matched as a phrase. All terms of a concept count as the same concept.
/// </summary>
public sealed record QueryConcept(string Original, IReadOnlyList<string> Terms);

/// <summary>Size and modification time of a source file, as cheap change evidence (plan §5.14).</summary>
public sealed record FileStamp(string Path, long Size, long ModifiedTicks);

/// <summary>What the index last saw for a file: its stamp and content hash.</summary>
public sealed record IndexedStamp(string Path, long Size, long ModifiedTicks, string Hash);

/// <summary>An authoritative file prepared for indexing: the parsed entry (null when invalid) and its findings.</summary>
public sealed record ScannedFile(
    FileStamp Stamp,
    string Hash,
    Storage.StoredEntry? Entry,
    Content.ContentAnalysis? Analysis,
    IReadOnlyList<ValidationIssue> Issues);

/// <summary>Changes to apply to one collection in one transaction.</summary>
public sealed record IndexChanges(CollectionId Collection, IReadOnlyList<ScannedFile> Upserts, IReadOnlyList<string> RemovedPaths);

/// <summary>An entry as the index holds it: enough for discovery and ranking without reading the source.</summary>
public sealed record IndexedEntry(EntryRef Ref, EntryMetadata Metadata, Applicability Applicability, Revision Revision, int SizeBytes, string Path);

/// <summary>Which fields matched one concept: descriptive fields (title, summary, tags, identifiers) or only the body.</summary>
public sealed record ConceptHit(int ConceptIndex, bool Descriptive);

/// <summary>A candidate with its lexical evidence. <see cref="LexicalRank"/> is 1-based among lexical matches, null for structural-only candidates.</summary>
public sealed record IndexCandidate(IndexedEntry Entry, int? LexicalRank, IReadOnlyList<ConceptHit> Hits, bool Incompatible);

/// <summary>
/// Candidate retrieval (plan §6.1 stages 2–3). The index applies every eligibility rule — collections,
/// kinds, tags, lifecycle, version incompatibility — before ranking, then unions lexical matches with
/// structural ones (file, symbol, package) and blocking uncertainties of the project.
/// </summary>
public sealed record CandidateQuery(
    QueryTerms Terms,
    QueryContext Context,
    IReadOnlyList<CollectionId> Collections,
    IReadOnlyList<EntryKind> Kinds,
    SearchFilters Filters,
    InclusionOptions Include,
    int Limit);

/// <summary>Candidates plus corpus statistics for weighting concepts by rarity.</summary>
public sealed record CandidateSet(IReadOnlyList<IndexCandidate> Candidates, IReadOnlyList<int> ConceptDocumentFrequency, int TotalEntries);

public sealed record IndexListQuery(
    IReadOnlyList<CollectionId> Collections,
    IReadOnlyList<EntryKind>? Kinds,
    SearchFilters Filters,
    InclusionOptions Include,
    bool BlockingOnly,
    EntryRef? After,
    int PageSize);

/// <summary>A link from one entry to another, as extracted at indexing time.</summary>
public sealed record IndexedLink(EntryRef Source, EntryRef Target, string Text);

public sealed record IndexDiagnostic(CollectionId Collection, string Path, ValidationIssue Issue);

public enum IndexFreshness
{
    Fresh,
    Stale,
    Unavailable,
}

public sealed record IndexStatus(int Entries, int Files, int Diagnostics, string Tokenizer, DateTimeOffset? LastSync);

/// <summary>Search index over the local collections (spec §3.3, abstraction 2). Derived and rebuildable.</summary>
public interface ISearchIndex
{
    ValueTask<IReadOnlyDictionary<string, IndexedStamp>> GetStampsAsync(CollectionId collection, CancellationToken cancellationToken);

    ValueTask ApplyAsync(IndexChanges changes, CancellationToken cancellationToken);

    /// <summary>Drops collections that are no longer configured.</summary>
    ValueTask RetainCollectionsAsync(IReadOnlyCollection<CollectionId> collections, CancellationToken cancellationToken);

    ValueTask ResetAsync(CancellationToken cancellationToken);

    ValueTask<CandidateSet> FindCandidatesAsync(CandidateQuery query, CancellationToken cancellationToken);

    ValueTask<IndexedEntry?> FindAsync(CollectionId collection, EntryId id, CancellationToken cancellationToken);

    ValueTask<Page<IndexedEntry>> ListAsync(IndexListQuery query, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<IndexedLink>> OutgoingLinksAsync(EntryRef source, CancellationToken cancellationToken);

    ValueTask<Page<IndexedLink>> BacklinksAsync(EntryRef target, EntryRef? after, int pageSize, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<IndexDiagnostic>> DiagnosticsAsync(CancellationToken cancellationToken);

    ValueTask<IndexStatus> GetStatusAsync(CancellationToken cancellationToken);
}
