using System.Security.Cryptography;
using AgentBrain.Application.Abstractions.Proposals;
using AgentBrain.Application.Abstractions.Support;
using AgentBrain.Application.Authorization;
using AgentBrain.Application.Common;
using AgentBrain.Application.Writing;
using AgentBrain.Domain.Access;
using AgentBrain.Domain.Identity;
using AgentBrain.Domain.Revisions;

namespace AgentBrain.Application.Operations.Proposals;

/// <summary>What a caller proposes: a new entry, a patch to an existing one, or a deletion.</summary>
public sealed record ChangeRequest(ChangeKind Kind, CollectionId Collection)
{
    public EntryId? Entry { get; init; }

    public Revision? BaseRevision { get; init; }

    public NewEntry? NewEntry { get; init; }

    public EntryPatch? Patch { get; init; }
}

/// <summary>
/// Proposals for curated and read-only collections (spec §10.2). Proposals are stored locally and never
/// change the target until accepted. A proposal against a read-only package is an explicit local record
/// referencing the package entry's identity and revision; it can be rejected but not applied here.
/// </summary>
public sealed class ProposalOperation(CollectionCatalog catalog, ChangePreparer preparer, IProposalStore store, IAuditLog audit, IndexFreshness freshness, TimeProvider time)
{
    public async ValueTask<Result<Proposal>> SubmitAsync(CallerContext caller, ChangeRequest request, string? note, IReadOnlyCollection<string> allowedSecretRules, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        var collection = catalog.Find(request.Collection);
        if (collection is null)
        {
            return Error.NotFound($"Collection '{request.Collection}' is not configured locally.");
        }

        var readable = CapabilityResolver.Require(caller, collection.Info, Capability.Read);
        if (!readable.IsSuccess)
        {
            return readable.Error;
        }

        var change = await PrepareAsync(preparer, collection, request, allowedSecretRules, cancellationToken);
        if (!change.IsSuccess)
        {
            return change.Error;
        }

        var proposal = new Proposal(NewId(time), change.Value, new Attribution(caller.Actor, caller.Via), time.GetUtcNow(), note);
        var saved = await store.SaveAsync(proposal, cancellationToken);
        if (!saved.IsSuccess)
        {
            return saved.Error;
        }

        await audit.AppendAsync(new AuditRecord(time.GetUtcNow(), proposal.ProposedBy, "proposal.submitted", proposal.Change.Target, "pending", proposal.Id), cancellationToken);
        return proposal;
    }

    public ValueTask<IReadOnlyList<Proposal>> ListAsync(ProposalStatus? status, CancellationToken cancellationToken) => store.ListAsync(status, cancellationToken);

    public async ValueTask<Result<Proposal>> ShowAsync(string id, CancellationToken cancellationToken) =>
        await store.FindAsync(id, cancellationToken) is { } proposal ? proposal : Error.NotFound($"Proposal '{id}' does not exist.");

    /// <summary>Applies a pending proposal after re-checking permissions, validation and the base revision.</summary>
    public async ValueTask<Result<Proposal>> AcceptAsync(CallerContext caller, string id, string? note, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var found = await PendingAsync(id, cancellationToken);
        if (!found.IsSuccess)
        {
            return found.Error;
        }

        var proposal = found.Value;
        var collection = catalog.Find(proposal.Change.Target.Collection);
        if (collection is null)
        {
            return Error.NotFound($"Collection '{proposal.Change.Target.Collection}' is not configured locally.");
        }

        var allowed = CapabilityResolver.Require(caller, collection.Info, Capability.Publish);
        if (!allowed.IsSuccess)
        {
            return allowed.Error;
        }

        var revalidated = ChangePreparer.Finish(collection, proposal.Change, []);
        if (!revalidated.IsSuccess)
        {
            return revalidated.Error;
        }

        var written = await ChangeApplier.ApplyAsync(collection, proposal.Change, cancellationToken);
        if (!written.IsSuccess)
        {
            await audit.AppendAsync(new AuditRecord(time.GetUtcNow(), new Attribution(caller.Actor, caller.Via), "proposal.accept", proposal.Change.Target, "failed", written.Error.Message), cancellationToken);
            return written.Error;
        }

        var accepted = proposal with { Status = ProposalStatus.Accepted, ReviewedBy = new Attribution(caller.Actor, caller.Via), ReviewedAt = time.GetUtcNow(), ReviewNote = note };
        await store.SaveAsync(accepted, cancellationToken);
        await audit.AppendAsync(new AuditRecord(time.GetUtcNow(), accepted.ReviewedBy!, "proposal.accepted", proposal.Change.Target, "accepted", proposal.Id), cancellationToken);
        await freshness.EnsureFreshAsync(cancellationToken);
        return accepted;
    }

    public async ValueTask<Result<Proposal>> RejectAsync(CallerContext caller, string id, string? reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var found = await PendingAsync(id, cancellationToken);
        if (!found.IsSuccess)
        {
            return found.Error;
        }

        var proposal = found.Value;
        var collection = catalog.Find(proposal.Change.Target.Collection);

        // Local proposals against read-only packages can always be rejected locally; otherwise rejecting is a publishing decision.
        if (collection is not null && !collection.Info.ReadOnly)
        {
            var allowed = CapabilityResolver.Require(caller, collection.Info, Capability.Publish);
            if (!allowed.IsSuccess)
            {
                return allowed.Error;
            }
        }

        var rejected = proposal with { Status = ProposalStatus.Rejected, ReviewedBy = new Attribution(caller.Actor, caller.Via), ReviewedAt = time.GetUtcNow(), ReviewNote = reason };
        await store.SaveAsync(rejected, cancellationToken);
        await audit.AppendAsync(new AuditRecord(time.GetUtcNow(), rejected.ReviewedBy!, "proposal.rejected", proposal.Change.Target, "rejected", proposal.Id), cancellationToken);
        return rejected;
    }

    /// <summary>Prepares a requested change against a collection; shared with immediate publishing.</summary>
    public static async ValueTask<Result<PreparedChange>> PrepareAsync(
        ChangePreparer preparer, WritableCollection collection, ChangeRequest request, IReadOnlyCollection<string> allowedSecretRules, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preparer);
        ArgumentNullException.ThrowIfNull(request);
        switch (request.Kind)
        {
            case ChangeKind.Create when request.NewEntry is not null:
                return await preparer.CreateAsync(collection, request.NewEntry, allowedSecretRules, cancellationToken);
            case ChangeKind.Update when request is { Entry: not null, BaseRevision: not null, Patch: not null }:
                return await preparer.UpdateAsync(collection, request.Entry, request.BaseRevision, request.Patch, allowedSecretRules, cancellationToken);
            case ChangeKind.Delete when request is { Entry: not null, BaseRevision: not null }:
                return await ChangePreparer.DeleteAsync(collection, request.Entry, request.BaseRevision, cancellationToken);
            default:
                return Error.Validation("Creates need the new entry; updates need the entry, its base revision and the changes; deletes need the entry and its base revision.");
        }
    }

    private async ValueTask<Result<Proposal>> PendingAsync(string id, CancellationToken cancellationToken)
    {
        var proposal = await store.FindAsync(id, cancellationToken);
        if (proposal is null)
        {
            return Error.NotFound($"Proposal '{id}' does not exist.");
        }

        return proposal.Status == ProposalStatus.Pending
            ? proposal
            : Error.Validation($"Proposal '{id}' was already {proposal.Status.ToString().ToLowerInvariant()}.");
    }

    private static string NewId(TimeProvider time)
    {
        Span<byte> random = stackalloc byte[3];
        RandomNumberGenerator.Fill(random);
        return $"p-{time.GetUtcNow():yyyyMMdd-HHmmss}-{Convert.ToHexStringLower(random)}";
    }
}
