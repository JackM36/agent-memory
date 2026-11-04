namespace AgentBrain.Contracts.V1;

// Wire contract, protocol version 1 (plan §7.2). Shared by CLI JSON output, MCP results and the HTTP API.
// Field names are part of the contract: change them only with a protocol version bump.

public static class ProtocolVersions
{
    public const int Protocol = 1;
    public const string Retrieval = "r1";

    /// <summary>HTTP header carrying the protocol version; a mismatch is rejected with protocol_version_unsupported.</summary>
    public const string Header = "Agent-Brain-Protocol";
}

public sealed record SourceDto(string Id, string State, string? Detail = null);

public sealed record SizeDto(int Bytes, string Tokens);

/// <summary>Revisions seen for the same identity in different sources (spec §8.4).</summary>
public sealed record ConflictDto(IReadOnlyList<string> Revisions, IReadOnlyList<string> Sources);

public sealed record PackageDto(string Name, string Versions);

/// <summary>Structured applicability, so remote clients can classify results against their own project.</summary>
public sealed record ApplicabilityDto(
    string Scope,
    IReadOnlyList<string> Projects,
    IReadOnlyList<string> Technologies,
    IReadOnlyList<PackageDto> Packages,
    IReadOnlyList<string> Platforms);

/// <summary>A discovery result: enough to decide whether to fetch, nothing more (spec §7.6).</summary>
public sealed record EntrySummaryDto(
    string Ref,
    string Kind,
    string Title,
    string Summary,
    IReadOnlyList<string> Tags,
    string Applies,
    string Source,
    SizeDto Size,
    string Rev,
    string From,
    ApplicabilityDto Applicability,
    string? Path = null,
    bool? Incompatible = null,
    ConflictDto? Conflict = null,
    string? Explain = null);

public sealed record OmittedDto(int Budget, int Blocking);

public sealed record BudgetDto(int MaxBytes, int UsedBytes, string Tokens);

public sealed record SearchResponseDto(
    int Protocol,
    string Retrieval,
    string Status,
    string Index,
    IReadOnlyList<SourceDto> Sources,
    IReadOnlyList<EntrySummaryDto> Blocking,
    IReadOnlyList<EntrySummaryDto> Results,
    OmittedDto Omitted,
    BudgetDto Budget);

public sealed record ListResponseDto(
    int Protocol,
    string Status,
    string Index,
    IReadOnlyList<SourceDto> Sources,
    IReadOnlyList<EntrySummaryDto> Items,
    string? NextCursor);

public sealed record LifecycleDto(string Publication, string? Archived, string? SupersededBy, string? Resolution, bool Blocking);

public sealed record ReferenceDto(string Target, string Text, string? Problem = null);

public sealed record TruncationDto(int OmittedBytes, IReadOnlyList<string> RemainingSections);

public sealed record EntryDto(
    EntrySummaryDto Entry,
    LifecycleDto Lifecycle,
    string Body,
    TruncationDto? Truncated,
    IReadOnlyList<ReferenceDto>? References);

public sealed record GetFailureDto(string Ref, string Code, string Message);

public sealed record EntriesResponseDto(int Protocol, IReadOnlyList<EntryDto> Entries, IReadOnlyList<GetFailureDto> Failures);

public sealed record ReferencesResponseDto(int Protocol, string Source, IReadOnlyList<ReferenceDto> References);

public sealed record BacklinkDto(string Source, string? Anchor, string Text);

public sealed record BacklinksResponseDto(int Protocol, string Target, IReadOnlyList<BacklinkDto> Backlinks, string? NextCursor);

public sealed record CollectionDto(string Id, string Title, IReadOnlyList<string> Capabilities, bool ReadOnly, string From);

public sealed record CollectionsResponseDto(int Protocol, IReadOnlyList<CollectionDto> Collections);

public sealed record SyncDto(int Added, int Updated, int Removed, int Unchanged);

public sealed record DiagnosticDto(string Collection, string Path, string Severity, string Code, string Field, string Message, int? Line);

public sealed record IndexResponseDto(
    int Protocol,
    int Entries,
    int Files,
    string Tokenizer,
    string? LastSync,
    SyncDto? Sync,
    IReadOnlyList<DiagnosticDto> Diagnostics);

public sealed record ErrorDto(int Protocol, string Code, string Message, IReadOnlyDictionary<string, string>? Details = null);
