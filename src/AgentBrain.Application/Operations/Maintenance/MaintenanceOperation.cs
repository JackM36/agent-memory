using AgentBrain.Application.Authorization;
using AgentBrain.Application.Capture;
using AgentBrain.Application.Common;
using AgentBrain.Application.Configuration;
using AgentBrain.Application.Retrieval;
using AgentBrain.Application.Writing;
using AgentBrain.Domain.Access;
using AgentBrain.Domain.Entries;
using AgentBrain.Domain.Identity;
using AgentBrain.Domain.Revisions;

namespace AgentBrain.Application.Operations.Maintenance;

/// <summary>Outcome of a direct write: the entry, its new revision (none after delete) and likely duplicates to review.</summary>
public sealed record WriteResult(EntryRef Ref, Revision? Revision, IReadOnlyList<SimilarEntry> Similar);

/// <summary>
/// Direct maintenance of designated project-memory collections (spec §10.1): create, update, archive,
/// supersede, resolve and delete, all revision-checked. Requires the <c>maintain</c> capability; curated
/// collections without it must go through proposals.
/// </summary>
public sealed class MaintenanceOperation(
    EffectiveConfiguration configuration,
    CollectionCatalog catalog,
    RetrievalCoordinator coordinator,
    ChangePreparer preparer,
    SimilarEntryFinder similar,
    IndexFreshness freshness)
{
    public async ValueTask<Result<WriteResult>> CreateAsync(CallerContext caller, CollectionId? collectionId, NewEntry entry, IReadOnlyCollection<string> allowedSecretRules, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var collection = Resolve(caller, collectionId ?? configuration.MemoryCollection);
        if (!collection.IsSuccess)
        {
            return collection.Error;
        }

        await freshness.EnsureFreshAsync(cancellationToken);
        var duplicates = await similar.FindAsync(collection.Value.Info.Id, entry.Title, entry.Summary, entry.Tags, null, cancellationToken);
        var change = await preparer.CreateAsync(collection.Value, entry, allowedSecretRules, cancellationToken);
        return change.IsSuccess ? await WriteAsync(collection.Value, change.Value, duplicates, cancellationToken) : change.Error;
    }

    public async ValueTask<Result<WriteResult>> UpdateAsync(CallerContext caller, EntryRef target, Revision baseRevision, EntryPatch patch, IReadOnlyCollection<string> allowedSecretRules, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(patch);
        var collection = Resolve(caller, target.Collection);
        if (!collection.IsSuccess)
        {
            return collection.Error;
        }

        foreach (var link in new[] { patch.SupersededBy, patch.ResolvedBy }.OfType<EntryRef>())
        {
            if (!await ExistsAsync(link, cancellationToken))
            {
                return Error.NotFound($"'{link}' does not exist, so it cannot be linked.");
            }
        }

        var change = await preparer.UpdateAsync(collection.Value, target.Entry, baseRevision, patch, allowedSecretRules, cancellationToken);
        return change.IsSuccess ? await WriteAsync(collection.Value, change.Value, [], cancellationToken) : change.Error;
    }

    public ValueTask<Result<WriteResult>> ArchiveAsync(CallerContext caller, EntryRef target, Revision baseRevision, CancellationToken cancellationToken) =>
        UpdateAsync(caller, target, baseRevision, new EntryPatch { Archive = true }, [], cancellationToken);

    public ValueTask<Result<WriteResult>> SupersedeAsync(CallerContext caller, EntryRef target, EntryRef replacement, Revision baseRevision, CancellationToken cancellationToken) =>
        UpdateAsync(caller, target, baseRevision, new EntryPatch { SupersededBy = replacement.WithoutAnchor() }, [], cancellationToken);

    /// <summary>Resolves an uncertainty with the memory that answers it; clears its blocking flag (plan §8.4).</summary>
    public async ValueTask<Result<WriteResult>> ResolveAsync(CallerContext caller, EntryRef uncertainty, EntryRef answer, Revision baseRevision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uncertainty);
        var collection = catalog.Find(uncertainty.Collection);
        if (collection is not null && await collection.Reader.FindAsync(uncertainty.Entry, null, cancellationToken) is { } current && current.Metadata.Kind != EntryKind.Uncertainty)
        {
            return Error.Validation($"'{uncertainty}' is a {WireNames.Kinds.Name(current.Metadata.Kind)}, not an uncertainty.");
        }

        return await UpdateAsync(caller, uncertainty, baseRevision, new EntryPatch { ResolvedBy = answer.WithoutAnchor() }, [], cancellationToken);
    }

    public async ValueTask<Result<WriteResult>> DeleteAsync(CallerContext caller, EntryRef target, Revision baseRevision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var collection = Resolve(caller, target.Collection);
        if (!collection.IsSuccess)
        {
            return collection.Error;
        }

        var change = await ChangePreparer.DeleteAsync(collection.Value, target.Entry, baseRevision, cancellationToken);
        return change.IsSuccess ? await WriteAsync(collection.Value, change.Value, [], cancellationToken) : change.Error;
    }

    /// <summary>The likely-duplicate check on its own (<c>brain similar</c>).</summary>
    public async ValueTask<Result<IReadOnlyList<SimilarEntry>>> SimilarAsync(CollectionId? collectionId, string title, string summary, IReadOnlyList<string> tags, CancellationToken cancellationToken)
    {
        var id = collectionId ?? configuration.MemoryCollection;
        if (id is null)
        {
            return Error.Validation("No collection given and no writes.memory configured.");
        }

        await freshness.EnsureFreshAsync(cancellationToken);
        return Result<IReadOnlyList<SimilarEntry>>.Success(await similar.FindAsync(id, title, summary, tags, null, cancellationToken));
    }

    private Result<WritableCollection> Resolve(CallerContext caller, CollectionId? id)
    {
        if (id is null)
        {
            return Error.Validation("No collection given and no writes.memory configured. Pass --collection or run brain init.");
        }

        var collection = catalog.Find(id);
        if (collection is null)
        {
            return Error.NotFound($"Collection '{id}' is not configured locally.");
        }

        var allowed = CapabilityResolver.Require(caller, collection.Info, Capability.Maintain);
        return allowed.IsSuccess ? collection : allowed.Error;
    }

    private async ValueTask<Result<WriteResult>> WriteAsync(WritableCollection collection, PreparedChange change, IReadOnlyList<SimilarEntry> duplicates, CancellationToken cancellationToken)
    {
        var written = await ChangeApplier.ApplyAsync(collection, change, cancellationToken);
        if (!written.IsSuccess)
        {
            return written.Error;
        }

        await freshness.EnsureFreshAsync(cancellationToken);
        return new WriteResult(change.Target, written.Value, duplicates);
    }

    private async ValueTask<bool> ExistsAsync(EntryRef target, CancellationToken cancellationToken)
    {
        if (catalog.Find(target.Collection) is { } local)
        {
            return await local.Reader.FindAsync(target.Entry, null, cancellationToken) is not null;
        }

        return coordinator.ProviderFor(target.Collection) is { } provider
            && (await provider.GetAsync(new GetRequest(target.WithoutAnchor(), 64, false), cancellationToken)).IsSuccess;
    }
}
