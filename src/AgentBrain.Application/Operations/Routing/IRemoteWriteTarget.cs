using AgentBrain.Application.Abstractions.Proposals;
using AgentBrain.Application.Common;
using AgentBrain.Application.Operations.Maintenance;
using AgentBrain.Application.Operations.Proposals;
using AgentBrain.Application.Operations.Publishing;
using AgentBrain.Application.Retrieval;
using AgentBrain.Domain.Identity;
using AgentBrain.Domain.Revisions;

namespace AgentBrain.Application.Operations.Routing;

/// <summary>The maintenance actions a host performs on an existing entry.</summary>
public enum MaintenanceAction
{
    Update,
    Archive,
    Supersede,
    Resolve,
}

/// <summary>
/// Writes to a remote host's collections. Requests are forwarded unprepared: the host is authoritative and runs
/// the same validation, ID generation, secret scan and revision checks as local writes. Nothing is prepared
/// twice, so the host never sees an ID or date the client chose.
/// </summary>
public interface IRemoteWriteTarget
{
    string SourceId { get; }

    /// <summary>Whether this host may serve the collection (listed on the connection, or the connection lists none).</summary>
    bool MayServe(CollectionId collection);

    /// <summary>The collection as the host describes it for this key, with effective capabilities; not found when the key cannot read it.</summary>
    ValueTask<Result<CollectionInfo>> DescribeAsync(CollectionId collection, CancellationToken cancellationToken);

    ValueTask<Result<WriteResult>> CreateAsync(ChangeRequest request, IReadOnlyCollection<string> allowedSecretRules, CancellationToken cancellationToken);

    ValueTask<Result<WriteResult>> MaintainAsync(MaintenanceAction action, ChangeRequest request, IReadOnlyCollection<string> allowedSecretRules, CancellationToken cancellationToken);

    ValueTask<Result<WriteResult>> DeleteAsync(EntryRef target, Revision baseRevision, CancellationToken cancellationToken);

    ValueTask<Result<ProposalView>> ProposeAsync(ChangeRequest request, string? note, IReadOnlyCollection<string> allowedSecretRules, CancellationToken cancellationToken);

    ValueTask<Result<IReadOnlyList<PublishedChange>>> PublishAsync(IReadOnlyList<ChangeRequest> requests, IReadOnlyCollection<string> allowedSecretRules, CancellationToken cancellationToken);

    ValueTask<Result<IReadOnlyList<ProposalView>>> ListProposalsAsync(ProposalStatus? status, CancellationToken cancellationToken);

    ValueTask<Result<ProposalView>> ShowProposalAsync(string id, CancellationToken cancellationToken);

    ValueTask<Result<ProposalView>> DecideAsync(string id, bool accept, string? note, CancellationToken cancellationToken);
}
