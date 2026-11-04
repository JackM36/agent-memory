using System.Globalization;
using AgentBrain.Application.Abstractions.Proposals;
using AgentBrain.Application.Abstractions.Support;
using AgentBrain.Application.Authorization;
using AgentBrain.Application.Common;
using AgentBrain.Application.Operations.Proposals;
using AgentBrain.Application.Writing;
using AgentBrain.Domain.Access;
using AgentBrain.Domain.Identity;
using AgentBrain.Domain.Revisions;

namespace AgentBrain.Application.Operations.Publishing;

/// <summary>A published change and its resulting revision (none after delete).</summary>
public sealed record PublishedChange(EntryRef Target, ChangeKind Kind, Revision? Revision);

/// <summary>State of one change in an interrupted batch.</summary>
public enum BatchItemState
{
    Applied,
    Pending,
    Conflict,
}

public sealed record BatchItemStatus(PreparedChange Change, BatchItemState State);

public sealed record BatchStatus(BatchRecord Batch, IReadOnlyList<BatchItemStatus> Items);

/// <summary>
/// Submit-and-publish (spec §10.3): applies changes immediately with an audit record. Disabled unless the
/// target collection enables it (<c>immediatePublish</c>) and the caller has <c>publish</c>; never bypasses
/// read-only status, validation, secret scanning or revision checks. A batch targets one collection: all
/// changes are prepared and checked first; SQLite storage applies them in one transaction, Markdown storage
/// applies them one by one under a batch marker (A6).
/// </summary>
public sealed class PublishOperation(
    CollectionCatalog catalog,
    ChangePreparer preparer,
    IBatchJournal journal,
    IAuditLog audit,
    IndexFreshness freshness,
    TimeProvider time)
{
    public const int MaxBatch = 50;

    public async ValueTask<Result<IReadOnlyList<PublishedChange>>> PublishAsync(
        CallerContext caller, IReadOnlyList<ChangeRequest> requests, IReadOnlyCollection<string> allowedSecretRules, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0 || requests.Count > MaxBatch)
        {
            return Error.Validation($"A publication needs 1–{MaxBatch} changes.");
        }

        var collectionIds = requests.Select(r => r.Collection).Distinct().ToList();
        if (collectionIds.Count != 1)
        {
            return Error.Validation("A batch targets one collection only.");
        }

        var collection = catalog.Find(collectionIds[0]);
        if (collection is null)
        {
            return Error.NotFound($"Collection '{collectionIds[0]}' is not configured locally.");
        }

        if (!collection.ImmediatePublish)
        {
            return new Error(ErrorCode.UnsupportedOperation,
                $"Immediate publishing is disabled for '{collection.Info.Id}'. Submit a proposal instead (brain propose).");
        }

        var allowed = CapabilityResolver.Require(caller, collection.Info, Capability.Publish);
        if (!allowed.IsSuccess)
        {
            return allowed.Error;
        }

        var changes = new List<PreparedChange>();
        foreach (var request in requests)
        {
            var prepared = await ProposalOperation.PrepareAsync(preparer, collection, request, allowedSecretRules, cancellationToken);
            if (!prepared.IsSuccess)
            {
                return prepared.Error;
            }

            changes.Add(prepared.Value);
        }

        if (changes.Select(c => c.Target).Distinct().Count() != changes.Count)
        {
            return Error.Validation("A batch may change each entry only once.");
        }

        var by = new Attribution(caller.Actor, caller.Via);
        var applied = collection.Writer is ITransactionalEntryWriter transactional
            ? await ApplyTransactionallyAsync(transactional, changes, cancellationToken)
            : await ApplyWithJournalAsync(collection, changes, by, cancellationToken);
        if (!applied.IsSuccess)
        {
            await audit.AppendAsync(new AuditRecord(time.GetUtcNow(), by, "publish", null, "failed", applied.Error.Message), cancellationToken);
            return applied.Error;
        }

        foreach (var change in applied.Value)
        {
            await audit.AppendAsync(new AuditRecord(time.GetUtcNow(), by, "publish." + change.Kind.ToString().ToLowerInvariant(), change.Target, "published", change.Revision?.Value), cancellationToken);
        }

        await freshness.EnsureFreshAsync(cancellationToken);
        return applied;
    }

    public async ValueTask<IReadOnlyList<BatchStatus>> StatusAsync(CancellationToken cancellationToken)
    {
        var statuses = new List<BatchStatus>();
        foreach (var batch in await journal.PendingAsync(cancellationToken))
        {
            var items = new List<BatchItemStatus>();
            var collection = catalog.Find(batch.Collection);
            foreach (var change in batch.Changes)
            {
                var current = collection is null ? null : await collection.Reader.FindAsync(change.Target.Entry, null, cancellationToken);
                items.Add(new BatchItemStatus(change, StateOf(change, current?.Revision)));
            }

            statuses.Add(new BatchStatus(batch, items));
        }

        return statuses;
    }

    /// <summary>Applies the changes of an interrupted batch that are still at their base revision; stops at the first conflict.</summary>
    public async ValueTask<Result<BatchStatus>> ResumeAsync(CallerContext caller, string id, CancellationToken cancellationToken)
    {
        var status = (await StatusAsync(cancellationToken)).FirstOrDefault(s => s.Batch.Id == id);
        if (status is null)
        {
            return Error.NotFound($"No interrupted batch '{id}'.");
        }

        var collection = catalog.Find(status.Batch.Collection);
        if (collection is null)
        {
            return Error.NotFound($"Collection '{status.Batch.Collection}' is not configured locally.");
        }

        var allowed = CapabilityResolver.Require(caller, collection.Info, Capability.Publish);
        if (!allowed.IsSuccess)
        {
            return allowed.Error;
        }

        if (status.Items.FirstOrDefault(i => i.State == BatchItemState.Conflict) is { } conflict)
        {
            return new Error(ErrorCode.RevisionConflict, $"'{conflict.Change.Target}' changed outside the batch; restore it or discard the batch marker.");
        }

        foreach (var item in status.Items.Where(i => i.State == BatchItemState.Pending))
        {
            var written = await ChangeApplier.ApplyAsync(collection, item.Change, cancellationToken);
            if (!written.IsSuccess)
            {
                return written.Error;
            }
        }

        await journal.CompleteAsync(id, cancellationToken);
        await audit.AppendAsync(new AuditRecord(time.GetUtcNow(), new Attribution(caller.Actor, caller.Via), "batch.resumed", null, "completed", id), cancellationToken);
        await freshness.EnsureFreshAsync(cancellationToken);
        return status with { Items = [.. status.Items.Select(i => i with { State = BatchItemState.Applied })] };
    }

    /// <summary>Forgets an interrupted batch after the owner restored the files manually (e.g. with Git).</summary>
    public ValueTask DiscardAsync(string id, CancellationToken cancellationToken) => journal.CompleteAsync(id, cancellationToken);

    private static BatchItemState StateOf(PreparedChange change, Revision? current) => change.Kind switch
    {
        ChangeKind.Create => current is null ? BatchItemState.Pending : current == change.NewRevision ? BatchItemState.Applied : BatchItemState.Conflict,
        ChangeKind.Update => current == change.NewRevision ? BatchItemState.Applied : current == change.BaseRevision ? BatchItemState.Pending : BatchItemState.Conflict,
        _ => current is null ? BatchItemState.Applied : current == change.BaseRevision ? BatchItemState.Pending : BatchItemState.Conflict,
    };

    private static async ValueTask<Result<IReadOnlyList<PublishedChange>>> ApplyTransactionallyAsync(
        ITransactionalEntryWriter writer, List<PreparedChange> changes, CancellationToken cancellationToken)
    {
        var result = await writer.ApplyAllAsync(changes, cancellationToken);
        return result.IsSuccess
            ? Result<IReadOnlyList<PublishedChange>>.Success([.. changes.Select((c, i) => new PublishedChange(c.Target, c.Kind, result.Value[i]))])
            : result.Error;
    }

    private async ValueTask<Result<IReadOnlyList<PublishedChange>>> ApplyWithJournalAsync(
        WritableCollection collection, List<PreparedChange> changes, Attribution by, CancellationToken cancellationToken)
    {
        var batchId = $"b-{time.GetUtcNow().ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)}";
        var journaled = changes.Count > 1;
        if (journaled)
        {
            await journal.BeginAsync(new BatchRecord(batchId, collection.Info.Id, time.GetUtcNow(), by, changes), cancellationToken);
        }

        var published = new List<PublishedChange>();
        foreach (var change in changes)
        {
            var written = await ChangeApplier.ApplyAsync(collection, change, cancellationToken);
            if (!written.IsSuccess)
            {
                var detail = journaled ? $" {published.Count} of {changes.Count} changes were applied; see brain batch status {batchId}." : "";
                return written.Error with { Message = written.Error.Message + detail };
            }

            published.Add(new PublishedChange(change.Target, change.Kind, written.Value));
        }

        if (journaled)
        {
            await journal.CompleteAsync(batchId, cancellationToken);
        }

        return published;
    }
}
