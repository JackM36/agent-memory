using AgentBrain.Application.Common;
using AgentBrain.Domain.Entries;
using AgentBrain.Domain.Identity;
using AgentBrain.Domain.Revisions;

namespace AgentBrain.Application.Abstractions.Storage;

/// <summary>
/// An entry as held by authoritative storage. <see cref="Locator"/> is the storage-specific location
/// (for Markdown, the path relative to the collection root), when meaningful.
/// </summary>
public sealed record StoredEntry(CollectionId Collection, EntryMetadata Metadata, Applicability Applicability, string Body, Revision Revision, string? Locator);

/// <summary>Reads authoritative entries of one collection (spec §3.3, abstraction 1).</summary>
public interface IEntryReader
{
    CollectionDescriptor Collection { get; }

    /// <summary>Directory or database location of the collection, for display.</summary>
    string Location { get; }

    /// <summary>False when storage cannot be written (e.g. a read-only package directory).</summary>
    bool IsWritable { get; }

    /// <summary>
    /// Reads the current authoritative entry. <paramref name="locatorHint"/> (e.g. the path the index
    /// last saw) is tried first and verified; the reader falls back to a full lookup if it is stale.
    /// </summary>
    ValueTask<StoredEntry?> FindAsync(EntryId id, string? locatorHint, CancellationToken cancellationToken);
}

/// <summary>
/// Writes authoritative entries of one collection. Every replacement and deletion is checked against the
/// revision the caller based it on; storage never silently overwrites newer content.
/// </summary>
public interface IEntryWriter
{
    ValueTask<Result<Revision>> CreateAsync(EntryMetadata metadata, string body, CancellationToken cancellationToken);

    ValueTask<Result<Revision>> ReplaceAsync(EntryId id, Revision baseRevision, EntryMetadata metadata, string body, CancellationToken cancellationToken);

    ValueTask<Result> DeleteAsync(EntryId id, Revision baseRevision, CancellationToken cancellationToken);
}
