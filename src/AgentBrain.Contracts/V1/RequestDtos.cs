namespace AgentBrain.Contracts.V1;

// Request bodies of the HTTP API (plan §10.4). The same shapes are sent by the remote provider client.

public sealed record ContextDto(
    string? Project = null,
    IReadOnlyList<string>? Technologies = null,
    IReadOnlyDictionary<string, string>? Packages = null,
    IReadOnlyList<string>? Files = null,
    IReadOnlyList<string>? Symbols = null,
    IReadOnlyList<string>? HintTags = null);

public sealed record FiltersDto(
    IReadOnlyList<string>? Kinds = null,
    IReadOnlyList<string>? Tags = null,
    bool TagsAny = false,
    IReadOnlyList<string>? Collections = null);

public sealed record SearchRequestDto(
    string Query,
    ContextDto? Context = null,
    FiltersDto? Filters = null,
    string? Relaxation = null,
    int? MaxResults = null,
    int? MaxBytes = null,
    int? MaxTokens = null,
    bool IncludeHistory = false,
    bool IncludeIncompatible = false,
    bool Explain = false);

public sealed record ListRequestDto(
    FiltersDto? Filters = null,
    bool IncludeHistory = false,
    bool IncludeDrafts = false,
    bool BlockingOnly = false,
    string? Cursor = null,
    int? PageSize = null);

public sealed record BatchGetRequestDto(IReadOnlyList<string> Refs, int? MaxBytes = null, bool WithReferences = false);

/// <summary>
/// One change, for direct maintenance, proposals and publishing. <c>Kind</c> is create, update or delete;
/// <c>Action</c> (maintenance only) is create, update, archive, supersede, resolve or delete.
/// </summary>
public sealed record ChangeRequestDto(
    string Kind,
    string Collection,
    string? Entry = null,
    string? BaseRevision = null,
    string? EntryKind = null,
    string? Title = null,
    string? Summary = null,
    string? Body = null,
    IReadOnlyList<string>? Tags = null,
    IReadOnlyList<string>? Files = null,
    IReadOnlyList<string>? Symbols = null,
    IReadOnlyList<string>? Evidence = null,
    bool Archive = false,
    string? SupersededBy = null,
    string? ResolvedBy = null,
    bool Blocking = false,
    string? Note = null,
    string? Action = null);

public sealed record PublishRequestDto(IReadOnlyList<ChangeRequestDto> Changes);

public sealed record DecisionDto(string? Note = null);

public sealed record HealthDto(string Status, int Protocol, string Version);
