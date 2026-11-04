using AgentBrain.Application.Abstractions.Proposals;
using AgentBrain.Application.Authorization;
using AgentBrain.Application.Common;
using AgentBrain.Application.Configuration;
using AgentBrain.Application.Operations.Maintenance;
using AgentBrain.Application.Operations.Proposals;
using AgentBrain.Application.Operations.Publishing;
using AgentBrain.Application.Retrieval;
using AgentBrain.Application.Writing;
using AgentBrain.Domain.Access;
using AgentBrain.Domain.Entries;
using AgentBrain.Domain.Identity;
using AgentBrain.Domain.Revisions;

namespace AgentBrain.Application.Operations.Routing;

/// <summary>The outcome of a save: written directly, or turned into a proposal because the target only accepts proposals.</summary>
public sealed record SaveOutcome(WriteResult? Written, ProposalView? Proposed)
{
    public static SaveOutcome Direct(WriteResult written) => new(written, null);

    public static SaveOutcome AsProposal(ProposalView proposal) => new(null, proposal);
}

/// <summary>Proposals from the local store and every reachable host, plus the hosts that could not be asked.</summary>
public sealed record ProposalListing(IReadOnlyList<ProposalView> Proposals, IReadOnlyList<string> Unavailable);

/// <summary>
/// The client-side entry point for every write (CLI and MCP). It picks the destination — the given collection,
/// else <c>writes.knowledge</c> for knowledge and <c>writes.memory</c> for everything else — and lets the
/// destination's capabilities decide how: <c>maintain</c> writes directly, <c>propose</c> records a proposal.
/// Local collections go through the local operations; remote ones are forwarded to their host, which applies
/// the same rules. The server does not use this router: its endpoints call the operations directly, so the
/// HTTP contract never silently turns a write into a proposal.
/// </summary>
public sealed class WriteRouter(
    EffectiveConfiguration configuration,
    CollectionCatalog catalog,
    MaintenanceOperation maintenance,
    ProposalOperation proposals,
    PublishOperation publishing,
    IReadOnlyList<IRemoteWriteTarget> remotes)
{
    private enum Mode
    {
        Direct,
        Propose,
    }

    /// <summary>Where a new entry of this kind goes when no collection is given.</summary>
    public CollectionId? DefaultTarget(EntryKind kind) =>
        kind == EntryKind.Knowledge ? configuration.KnowledgeCollection ?? configuration.MemoryCollection : configuration.MemoryCollection;

    public async ValueTask<Result<SaveOutcome>> CreateAsync(
        CallerContext caller, CollectionId? collection, NewEntry entry, string? note, IReadOnlyCollection<string> allowedSecretRules, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var id = collection ?? DefaultTarget(entry.Kind);
        if (id is null)
        {
            return Error.Validation(entry.Kind == EntryKind.Knowledge
                ? "No collection given and neither writes.knowledge nor writes.memory is configured. Pass a collection or run brain init."
                : "No collection given and no writes.memory configured. Pass a collection or run brain init.");
        }

        var request = new ChangeRequest(ChangeKind.Create, id) { NewEntry = entry };
        var target = await LocateAsync(caller, id, cancellationToken);
        if (!target.IsSuccess)
        {
            return target.Error;
        }

        var (remote, mode) = target.Value;
        if (mode == Mode.Propose)
        {
            return await ProposeToAsync(caller, remote, request, note, allowedSecretRules, cancellationToken);
        }

        var written = remote is null
            ? await maintenance.CreateAsync(caller, id, entry, allowedSecretRules, cancellationToken)
            : await remote.CreateAsync(request, allowedSecretRules, cancellationToken);
        return written.IsSuccess ? SaveOutcome.Direct(written.Value) : written.Error;
    }

    public async ValueTask<Result<SaveOutcome>> ChangeAsync(
        CallerContext caller, MaintenanceAction action, EntryRef entry, Revision baseRevision, EntryPatch patch, string? note,
        IReadOnlyCollection<string> allowedSecretRules, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(patch);
        var request = new ChangeRequest(ChangeKind.Update, entry.Collection) { Entry = entry.Entry, BaseRevision = baseRevision, Patch = patch };
        var target = await LocateAsync(caller, entry.Collection, cancellationToken);
        if (!target.IsSuccess)
        {
            return target.Error;
        }

        var (remote, mode) = target.Value;
        if (mode == Mode.Propose)
        {
            return await ProposeToAsync(caller, remote, request, note, allowedSecretRules, cancellationToken);
        }

        var written = remote is not null
            ? await remote.MaintainAsync(action, request, allowedSecretRules, cancellationToken)
            : action == MaintenanceAction.Resolve && patch.ResolvedBy is { } answer
                ? await maintenance.ResolveAsync(caller, entry, answer, baseRevision, cancellationToken)
                : await maintenance.UpdateAsync(caller, entry, baseRevision, patch, allowedSecretRules, cancellationToken);
        return written.IsSuccess ? SaveOutcome.Direct(written.Value) : written.Error;
    }

    public async ValueTask<Result<SaveOutcome>> DeleteAsync(CallerContext caller, EntryRef entry, Revision baseRevision, string? note, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var target = await LocateAsync(caller, entry.Collection, cancellationToken);
        if (!target.IsSuccess)
        {
            return target.Error;
        }

        var (remote, mode) = target.Value;
        if (mode == Mode.Propose)
        {
            var request = new ChangeRequest(ChangeKind.Delete, entry.Collection) { Entry = entry.Entry, BaseRevision = baseRevision };
            return await ProposeToAsync(caller, remote, request, note, [], cancellationToken);
        }

        var deleted = remote is null
            ? await maintenance.DeleteAsync(caller, entry, baseRevision, cancellationToken)
            : await remote.DeleteAsync(entry, baseRevision, cancellationToken);
        return deleted.IsSuccess ? SaveOutcome.Direct(deleted.Value) : deleted.Error;
    }

    /// <summary>An explicit proposal, local or remote, whatever the target's capabilities.</summary>
    public async ValueTask<Result<ProposalView>> ProposeAsync(
        CallerContext caller, ChangeRequest request, string? note, IReadOnlyCollection<string> allowedSecretRules, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (catalog.Find(request.Collection) is not null)
        {
            var local = await proposals.SubmitAsync(caller, request, note, allowedSecretRules, cancellationToken);
            return local.IsSuccess ? ProposalView.From(local.Value) : local.Error;
        }

        var remote = await FindRemoteAsync(request.Collection, cancellationToken);
        return remote.IsSuccess
            ? await remote.Value.Remote.ProposeAsync(request, note, allowedSecretRules, cancellationToken)
            : remote.Error;
    }

    /// <summary>Immediate publishing (explicit user approval required by the agent guide); the batch targets one collection.</summary>
    public async ValueTask<Result<IReadOnlyList<PublishedChange>>> PublishAsync(
        CallerContext caller, IReadOnlyList<ChangeRequest> requests, IReadOnlyCollection<string> allowedSecretRules, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0 || catalog.Find(requests[0].Collection) is not null)
        {
            return await publishing.PublishAsync(caller, requests, allowedSecretRules, cancellationToken);
        }

        var remote = await FindRemoteAsync(requests[0].Collection, cancellationToken);
        return remote.IsSuccess
            ? await remote.Value.Remote.PublishAsync(requests, allowedSecretRules, cancellationToken)
            : remote.Error;
    }

    public async ValueTask<ProposalListing> ListProposalsAsync(ProposalStatus? status, CancellationToken cancellationToken)
    {
        var all = (await proposals.ListAsync(status, cancellationToken)).Select(p => ProposalView.From(p)).ToList();
        var unavailable = new List<string>();
        foreach (var remote in remotes)
        {
            var listed = await remote.ListProposalsAsync(status, cancellationToken);
            if (listed.IsSuccess)
            {
                all.AddRange(listed.Value);
            }
            else
            {
                unavailable.Add($"{remote.SourceId}: {listed.Error.Message}");
            }
        }

        return new ProposalListing(all, unavailable);
    }

    public async ValueTask<Result<ProposalView>> ShowProposalAsync(string qualifiedId, CancellationToken cancellationToken)
    {
        var (source, id) = ProposalView.Parse(qualifiedId);
        if (source is null)
        {
            var local = await proposals.ShowAsync(id, cancellationToken);
            return local.IsSuccess ? ProposalView.From(local.Value, withBody: true) : local.Error;
        }

        return Remote(source) is { } remote ? await remote.ShowProposalAsync(id, cancellationToken) : UnknownSource(source);
    }

    public async ValueTask<Result<ProposalView>> DecideAsync(CallerContext caller, string qualifiedId, bool accept, string? note, CancellationToken cancellationToken)
    {
        var (source, id) = ProposalView.Parse(qualifiedId);
        if (source is null)
        {
            var local = accept
                ? await proposals.AcceptAsync(caller, id, note, cancellationToken)
                : await proposals.RejectAsync(caller, id, note, cancellationToken);
            return local.IsSuccess ? ProposalView.From(local.Value) : local.Error;
        }

        return Remote(source) is { } remote ? await remote.DecideAsync(id, accept, note, cancellationToken) : UnknownSource(source);
    }

    /// <summary>Finds the collection, locally first, and decides how a save reaches it from the caller's capabilities there.</summary>
    private async ValueTask<Result<(IRemoteWriteTarget? Remote, Mode Mode)>> LocateAsync(CallerContext caller, CollectionId id, CancellationToken cancellationToken)
    {
        if (catalog.Find(id) is { } local)
        {
            var mode = Decide(local.Info, caller.Effective(local.Info));
            return mode.IsSuccess ? (null, mode.Value) : mode.Error;
        }

        var remote = await FindRemoteAsync(id, cancellationToken);
        if (!remote.IsSuccess)
        {
            return remote.Error;
        }

        var remoteMode = Decide(remote.Value.Info, remote.Value.Info.Capabilities);
        return remoteMode.IsSuccess ? (remote.Value.Remote, remoteMode.Value) : remoteMode.Error;
    }

    private static Result<Mode> Decide(CollectionInfo info, Capability effective)
    {
        if (info.ReadOnly)
        {
            return new Error(ErrorCode.ReadOnly, $"Collection '{info.Id}' is read-only.");
        }

        if (effective.HasFlag(Capability.Maintain))
        {
            return Mode.Direct;
        }

        return effective.HasFlag(Capability.Propose)
            ? Mode.Propose
            : new Error(ErrorCode.Forbidden, $"Collection '{info.Id}' accepts neither direct changes nor proposals from this caller.");
    }

    private async ValueTask<Result<SaveOutcome>> ProposeToAsync(
        CallerContext caller, IRemoteWriteTarget? remote, ChangeRequest request, string? note, IReadOnlyCollection<string> allowedSecretRules, CancellationToken cancellationToken)
    {
        if (remote is not null)
        {
            var proposed = await remote.ProposeAsync(request, note, allowedSecretRules, cancellationToken);
            return proposed.IsSuccess ? SaveOutcome.AsProposal(proposed.Value) : proposed.Error;
        }

        var local = await proposals.SubmitAsync(caller, request, note, allowedSecretRules, cancellationToken);
        return local.IsSuccess ? SaveOutcome.AsProposal(ProposalView.From(local.Value)) : local.Error;
    }

    /// <summary>The host serving a collection: connections that list it first, then connections that serve everything their key reads.</summary>
    private async ValueTask<Result<(IRemoteWriteTarget Remote, CollectionInfo Info)>> FindRemoteAsync(CollectionId id, CancellationToken cancellationToken)
    {
        Error? failure = null;
        foreach (var remote in remotes.Where(r => r.MayServe(id)))
        {
            var described = await remote.DescribeAsync(id, cancellationToken);
            if (described.IsSuccess)
            {
                return (remote, described.Value);
            }

            if (described.Error.Code != ErrorCode.NotFound)
            {
                failure ??= described.Error;
            }
        }

        return failure ?? Error.NotFound($"Collection '{id}' is not configured locally or served by any connection.");
    }

    private IRemoteWriteTarget? Remote(string source) => remotes.FirstOrDefault(r => r.SourceId == source);

    private static Error UnknownSource(string source) => Error.NotFound($"No connection named '{source}'.");
}
