using AgentBrain.Domain.Identity;
using AgentBrain.Domain.Metadata;

namespace AgentBrain.Domain.Entries;

/// <summary>When and how an entry was last verified.</summary>
public sealed record Verification(DateOnly On, VerificationMethod By);

/// <summary>
/// Entry metadata, schema version 1 (plan §5.4). Holds values as authored (after normalisation);
/// collection defaults are applied by <see cref="EntryValidator"/>, not stored here.
/// </summary>
public sealed record EntryMetadata
{
    public const int CurrentSchema = 1;

    public required EntryId Id { get; init; }

    public required EntryKind Kind { get; init; }

    public required string Title { get; init; }

    public required string Summary { get; init; }

    public int Schema { get; init; } = CurrentSchema;

    public IReadOnlyList<string> Tags { get; init; } = [];

    public ApplicabilityOverrides? Applies { get; init; }

    public IReadOnlyList<string> Files { get; init; } = [];

    public IReadOnlyList<string> Symbols { get; init; } = [];

    public ProvenanceSource? Source { get; init; }

    public IReadOnlyList<string> Evidence { get; init; } = [];

    public Verification? Verified { get; init; }

    public Publication? Publication { get; init; }

    public DateOnly? Archived { get; init; }

    public EntryRef? SupersededBy { get; init; }

    public EntryRef? Origin { get; init; }

    public Resolution? Resolution { get; init; }

    public bool? Blocking { get; init; }

    public EntryRef? ResolvedBy { get; init; }

    public DateOnly? Created { get; init; }

    public DateOnly? Updated { get; init; }

    /// <summary><c>x-</c> prefixed fields: preserved on write, ignored by the system, excluded from revisions.</summary>
    public IReadOnlyList<KeyValuePair<string, MetadataNode>> Extensions { get; init; } = [];

    /// <summary>True when the entry is retired from default search (archived or superseded).</summary>
    public bool IsRetired => Archived is not null || SupersededBy is not null;

    /// <summary>True for an unresolved uncertainty flagged as blocking (A4).</summary>
    public bool IsBlockingUncertainty => Kind == EntryKind.Uncertainty && Resolution == Entries.Resolution.Unresolved && Blocking == true;
}
