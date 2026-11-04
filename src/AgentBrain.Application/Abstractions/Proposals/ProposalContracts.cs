using AgentBrain.Application.Common;
using AgentBrain.Application.Writing;
using AgentBrain.Domain.Identity;
using AgentBrain.Domain.Revisions;

namespace AgentBrain.Application.Abstractions.Proposals;

public enum ProposalStatus
{
    Pending,
    Accepted,
    Rejected,
}

/// <summary>Who did something: a user, an agent session, or an API key label, and through which host.</summary>
public sealed record Attribution(string Actor, string? Via = null)
{
    public override string ToString() => Via is null ? Actor : $"{Actor} via {Via}";
}

/// <summary>
/// A proposed change (spec §10.2). It holds the fully prepared change, including the base revision, so
/// acceptance can reject stale proposals. The target is never touched until a proposal is accepted.
/// </summary>
public sealed record Proposal(
    string Id,
    PreparedChange Change,
    Attribution ProposedBy,
    DateTimeOffset CreatedAt,
    string? Note = null,
    ProposalStatus Status = ProposalStatus.Pending,
    Attribution? ReviewedBy = null,
    DateTimeOffset? ReviewedAt = null,
    string? ReviewNote = null);

public interface IProposalStore
{
    ValueTask<Result> SaveAsync(Proposal proposal, CancellationToken cancellationToken);

    ValueTask<Proposal?> FindAsync(string id, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<Proposal>> ListAsync(ProposalStatus? status, CancellationToken cancellationToken);
}

/// <summary>A multi-file publication in progress: enough to tell, after a crash, which changes were applied (A6).</summary>
public sealed record BatchRecord(string Id, CollectionId Collection, DateTimeOffset StartedAt, Attribution By, IReadOnlyList<PreparedChange> Changes);

/// <summary>
/// Batch markers for Markdown publications (plan §5.12, A6): written before the first change and removed
/// after the last. A leftover marker means the batch was interrupted.
/// </summary>
public interface IBatchJournal
{
    ValueTask BeginAsync(BatchRecord batch, CancellationToken cancellationToken);

    ValueTask CompleteAsync(string id, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<BatchRecord>> PendingAsync(CancellationToken cancellationToken);
}

/// <summary>Storage that can apply a whole batch atomically (SQLite). Markdown storage cannot and uses the journal.</summary>
public interface ITransactionalEntryWriter
{
    ValueTask<Result<IReadOnlyList<Revision?>>> ApplyAllAsync(IReadOnlyList<PreparedChange> changes, CancellationToken cancellationToken);
}
