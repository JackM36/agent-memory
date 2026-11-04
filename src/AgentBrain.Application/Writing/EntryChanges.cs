using AgentBrain.Domain.Entries;
using AgentBrain.Domain.Identity;
using AgentBrain.Domain.Revisions;

namespace AgentBrain.Application.Writing;

/// <summary>A new entry as a caller describes it. The ID is generated from the title unless given.</summary>
public sealed record NewEntry(EntryKind Kind, string Title, string Summary, string Body)
{
    public EntryId? Id { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    public IReadOnlyList<string> Files { get; init; } = [];

    public IReadOnlyList<string> Symbols { get; init; } = [];

    public IReadOnlyList<string> Evidence { get; init; } = [];

    public ApplicabilityOverrides? Applies { get; init; }

    public ProvenanceSource? Source { get; init; }

    /// <summary>For uncertainties: whether guessing could cause harm (A4).</summary>
    public bool Blocking { get; init; }
}

/// <summary>
/// Changes to an existing entry. Null fields stay as they are. Lifecycle changes (archive, supersede,
/// resolve) are patches too, so proposals and direct maintenance share one path.
/// </summary>
public sealed record EntryPatch
{
    public string? Title { get; init; }

    public string? Summary { get; init; }

    public string? Body { get; init; }

    public IReadOnlyList<string>? Tags { get; init; }

    public IReadOnlyList<string>? Files { get; init; }

    public IReadOnlyList<string>? Symbols { get; init; }

    public IReadOnlyList<string>? Evidence { get; init; }

    public Verification? Verified { get; init; }

    public bool Archive { get; init; }

    public EntryRef? SupersededBy { get; init; }

    public EntryRef? ResolvedBy { get; init; }

    public bool? Blocking { get; init; }

    public Publication? Publication { get; init; }
}

public enum ChangeKind
{
    Create,
    Update,
    Delete,
}

/// <summary>
/// A fully prepared change to one entry: validated, secret-scanned, ready to write. Proposals store it;
/// publishing and acceptance apply it after re-checking the base revision.
/// </summary>
public sealed record PreparedChange(ChangeKind Kind, EntryRef Target, Revision? BaseRevision, EntryMetadata? Metadata, string? Body)
{
    public Revision? NewRevision => Metadata is null || Body is null ? null : RevisionCalculator.Compute(Metadata, Body);
}
