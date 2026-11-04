using AgentBrain.Application.Common;
using AgentBrain.Application.Retrieval.Ranking;
using AgentBrain.Domain.Access;
using AgentBrain.Domain.Entries;
using AgentBrain.Domain.Identity;
using AgentBrain.Domain.Revisions;

namespace AgentBrain.Application.Retrieval;

/// <summary>
/// Task context for a search (spec §7.1). Stable values come from configuration, task values from the
/// request. File paths are project-relative. Hint tags boost; they never filter.
/// </summary>
public sealed record QueryContext(
    string? ProjectId = null,
    IReadOnlyList<string>? Technologies = null,
    IReadOnlyDictionary<string, string>? Packages = null,
    IReadOnlyList<string>? Files = null,
    IReadOnlyList<string>? Symbols = null,
    IReadOnlyList<string>? HintTags = null)
{
    public static readonly QueryContext Empty = new();
}

public enum TagMatch
{
    All,
    Any,
}

/// <summary>Exact filters: they restrict the candidate set and are never relaxed.</summary>
public sealed record SearchFilters(
    IReadOnlyList<EntryKind>? Kinds = null,
    IReadOnlyList<string>? Tags = null,
    TagMatch TagMatch = TagMatch.All,
    IReadOnlyList<CollectionId>? Collections = null)
{
    public static readonly SearchFilters None = new();
}

/// <summary>Hard limits on one response. The byte limit is authoritative; tokens are an estimate converted to bytes.</summary>
public sealed record ResponseLimits(int MaxResults, int MaxBytes, int? MaxTokens = null);

/// <summary>Which normally hidden content a search may include (plan §7.1).</summary>
public sealed record InclusionOptions(bool History = false, bool Incompatible = false, bool Drafts = false)
{
    public static readonly InclusionOptions Default = new();
}

public sealed record SearchRequest(
    string Query,
    QueryContext Context,
    SearchFilters Filters,
    Relaxation Relaxation,
    ResponseLimits Limits,
    InclusionOptions Include,
    int CandidateLimit,
    bool Explain = false);

/// <summary>What a discovery result tells the caller so it can decide whether to fetch (spec §7.6).</summary>
public sealed record EntrySummary(
    EntryRef Ref,
    EntryKind Kind,
    string Title,
    string Summary,
    IReadOnlyList<string> Tags,
    Applicability Applicability,
    ProvenanceSource Source,
    int SizeBytes,
    Revision Revision,
    string? Locator);

/// <summary>
/// A summary with its rank (1-based) within one source, how it relates to the query context, and the
/// source that returned it. <see cref="Explanation"/> is filled only in explain mode.
/// </summary>
public sealed record RankedEntry(
    EntrySummary Entry,
    int Rank,
    ApplicabilityClass Applicability,
    string SourceId,
    bool Incompatible = false,
    string? Explanation = null,
    RevisionConflict? Conflict = null);

/// <summary>The same identity seen with different revisions in different sources (spec §8.4). Never resolved silently.</summary>
public sealed record RevisionConflict(IReadOnlyList<string> Sources, IReadOnlyList<Revision> Revisions);

public enum SourceState
{
    Ok,
    Timeout,
    Unavailable,
    Unauthorized,
}

public sealed record SourceStatus(string SourceId, SourceState State, string? Detail = null);

/// <summary>One source's admitted, ranked results plus the blocking-uncertainty pass (A4).</summary>
public sealed record ProviderSearchResult(
    IReadOnlyList<RankedEntry> Results,
    IReadOnlyList<RankedEntry> Blocking,
    bool IndexStale,
    IReadOnlyList<string>? Notes = null);

public sealed record ListRequest(
    SearchFilters Filters,
    InclusionOptions Include,
    bool BlockingOnly,
    string? Cursor,
    int PageSize);

public sealed record GetRequest(EntryRef Ref, int MaxBytes, bool WithReferences);

/// <summary>Lifecycle facts a get reports explicitly instead of silently substituting a replacement (spec §6.3).</summary>
public sealed record LifecycleInfo(Publication Publication, DateOnly? Archived, EntryRef? SupersededBy, Resolution? Resolution, bool Blocking);

/// <summary>Why a reference could not be followed (plan §7.6).</summary>
public enum ReferenceProblem
{
    TargetMissing,
    CollectionUnconfigured,
    SourceUnavailable,
    SectionMissing,
}

public sealed record OutgoingReference(EntryRef Target, string LinkText, ReferenceProblem? Problem);

/// <summary>Content returned by get: the whole body or one section, with explicit truncation.</summary>
public sealed record EntryContent(
    EntrySummary Entry,
    LifecycleInfo Lifecycle,
    string Body,
    int OmittedBytes,
    IReadOnlyList<string> RemainingSections,
    IReadOnlyList<OutgoingReference>? References,
    string SourceId);

public sealed record Backlink(EntryRef Source, string? TargetAnchor, string LinkText);

public sealed record CollectionInfo(CollectionId Id, string Title, Capability Capabilities, bool ReadOnly, string SourceId);

/// <summary>Outcome of searching one or more sources: the input to response budgeting and rendering.</summary>
public sealed record SearchOutcome(
    IReadOnlyList<RankedEntry> Results,
    IReadOnlyList<RankedEntry> Blocking,
    IReadOnlyList<SourceStatus> Sources,
    bool IndexStale,
    IReadOnlyList<string>? Notes = null)
{
    public bool IsPartial => Sources.Any(s => s.State != SourceState.Ok);
}

/// <summary>
/// A budgeted response: what fits, and how much was left out and why. <see cref="ByteLimit"/> is the
/// effective hard limit (the smaller of the byte limit and the token budget converted to bytes);
/// <see cref="UsedBytes"/> is the rendered size, filled in by the budgeter.
/// </summary>
public sealed record SearchResponse(
    SearchOutcome Outcome,
    IReadOnlyList<RankedEntry> Results,
    IReadOnlyList<RankedEntry> Blocking,
    int OmittedForBudget,
    int BlockingOmitted,
    int ByteLimit,
    int UsedBytes);

/// <summary>A page of list results with per-source status.</summary>
public sealed record ListResponse(IReadOnlyList<RankedEntry> Items, string? NextCursor, IReadOnlyList<SourceStatus> Sources, bool IndexStale);

public static class PageDefaults
{
    public const int PageSize = 50;
    public const int MaxPageSize = 500;
}

/// <summary>Convenience for failures shared across operations.</summary>
public static class RetrievalErrors
{
    public static Error SectionNotFound(EntryRef reference) =>
        new(ErrorCode.SectionNotFound, $"'{reference}' has no section '{reference.Anchor}'.");
}
