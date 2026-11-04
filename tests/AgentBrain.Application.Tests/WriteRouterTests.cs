using AgentBrain.Application.Abstractions.Proposals;
using AgentBrain.Application.Authorization;
using AgentBrain.Application.Common;
using AgentBrain.Application.Configuration;
using AgentBrain.Application.Operations;
using AgentBrain.Application.Operations.Maintenance;
using AgentBrain.Application.Operations.Proposals;
using AgentBrain.Application.Operations.Publishing;
using AgentBrain.Application.Operations.Routing;
using AgentBrain.Application.Retrieval;
using AgentBrain.Application.Writing;
using AgentBrain.Domain.Access;
using AgentBrain.Domain.Entries;
using AgentBrain.Domain.Identity;
using AgentBrain.Domain.Revisions;

namespace AgentBrain.Application.Tests;

/// <summary>Where saves go and how: the router's choices, with a fake host standing in for the server.</summary>
public sealed class WriteRouterTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "brain-router-tests", "game"));
    private static readonly CallerContext Caller = CallerContext.Local("test");
    private static readonly Revision Base = Revision.Parse("r1-" + new string('a', 26));

    private sealed class FakeHost(string id, params CollectionInfo[] collections) : IRemoteWriteTarget
    {
        public List<string> Calls { get; } = [];

        public bool ListFails { get; init; }

        public string SourceId => id;

        public bool MayServe(CollectionId collection) => true;

        public ValueTask<Result<CollectionInfo>> DescribeAsync(CollectionId collection, CancellationToken cancellationToken) =>
            ValueTask.FromResult(collections.FirstOrDefault(c => c.Id == collection) is { } info
                ? Result<CollectionInfo>.Success(info)
                : Error.NotFound("not here"));

        public ValueTask<Result<WriteResult>> CreateAsync(ChangeRequest request, IReadOnlyCollection<string> allowedSecretRules, CancellationToken cancellationToken)
        {
            Calls.Add($"create {request.Collection} {request.NewEntry!.Kind}");
            return ValueTask.FromResult<Result<WriteResult>>(new WriteResult(new EntryRef(request.Collection, EntryId.Parse("x")), Base, []));
        }

        public ValueTask<Result<WriteResult>> MaintainAsync(MaintenanceAction action, ChangeRequest request, IReadOnlyCollection<string> allowedSecretRules, CancellationToken cancellationToken)
        {
            Calls.Add($"{action} {request.Collection}/{request.Entry}");
            return ValueTask.FromResult<Result<WriteResult>>(new WriteResult(new EntryRef(request.Collection, request.Entry!), Base, []));
        }

        public ValueTask<Result<WriteResult>> DeleteAsync(EntryRef target, Revision baseRevision, CancellationToken cancellationToken)
        {
            Calls.Add($"delete {target}");
            return ValueTask.FromResult<Result<WriteResult>>(new WriteResult(target, null, []));
        }

        public ValueTask<Result<ProposalView>> ProposeAsync(ChangeRequest request, string? note, IReadOnlyCollection<string> allowedSecretRules, CancellationToken cancellationToken)
        {
            Calls.Add($"propose {request.Kind} {request.Collection}");
            return ValueTask.FromResult<Result<ProposalView>>(View("p-1"));
        }

        public ValueTask<Result<IReadOnlyList<PublishedChange>>> PublishAsync(IReadOnlyList<ChangeRequest> requests, IReadOnlyCollection<string> allowedSecretRules, CancellationToken cancellationToken)
        {
            Calls.Add($"publish {requests[0].Collection}");
            return ValueTask.FromResult<Result<IReadOnlyList<PublishedChange>>>(Result<IReadOnlyList<PublishedChange>>.Success([]));
        }

        public ValueTask<Result<IReadOnlyList<ProposalView>>> ListProposalsAsync(ProposalStatus? status, CancellationToken cancellationToken) =>
            ValueTask.FromResult(ListFails
                ? new Error(ErrorCode.ProviderUnavailable, "down")
                : Result<IReadOnlyList<ProposalView>>.Success([View("p-1")]));

        public ValueTask<Result<ProposalView>> ShowProposalAsync(string proposalId, CancellationToken cancellationToken) => ValueTask.FromResult<Result<ProposalView>>(View(proposalId));

        public ValueTask<Result<ProposalView>> DecideAsync(string proposalId, bool accept, string? note, CancellationToken cancellationToken)
        {
            Calls.Add($"{(accept ? "accept" : "reject")} {proposalId}");
            return ValueTask.FromResult<Result<ProposalView>>(View(proposalId) with { Status = accept ? ProposalStatus.Accepted : ProposalStatus.Rejected });
        }

        private ProposalView View(string proposalId) =>
            new(proposalId, ProposalStatus.Pending, ChangeKind.Create, EntryRef.Parse("library/x"), null, "T", "S", "someone", DateTimeOffset.UnixEpoch, Source: id);
    }

    private sealed class EmptyStore : IProposalStore
    {
        public ValueTask<Result> SaveAsync(Proposal proposal, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<Proposal?> FindAsync(string id, CancellationToken cancellationToken) => ValueTask.FromResult<Proposal?>(null);

        public ValueTask<IReadOnlyList<Proposal>> ListAsync(ProposalStatus? status, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<Proposal>>([]);
    }

    private static CollectionInfo Remote(string id, Capability capabilities, bool readOnly = false) => new(CollectionId.Parse(id), id, capabilities, readOnly, "personal");

    private static EffectiveConfiguration Configuration(string? knowledge)
    {
        var project = new ConfigDocument
        {
            Project = new ProjectSection("space-game"),
            Connections = new Dictionary<string, ConnectionDefinition> { ["personal"] = new("https://kb.example.com", "KEY") },
            Writes = knowledge is null ? null : new WritesSection(Knowledge: knowledge),
        };
        var configDir = Path.Combine(Root, ".agent-brain");
        var resolution = ConfigLayering.Resolve(new ConfigurationInput(
            new ProjectLocation(Root, configDir), [new LoadedConfigDocument(ConfigLevel.Project, Path.Combine(configDir, "config.json"), configDir, project)]));
        Assert.False(resolution.HasErrors, string.Join("; ", resolution.Diagnostics));
        return resolution.Configuration;
    }

    /// <summary>A router with no local collections: every write goes to the fake host. The local operations are never reached.</summary>
    private static WriteRouter Router(FakeHost host, string? knowledge = "library")
    {
        var configuration = Configuration(knowledge);
        var catalog = new CollectionCatalog([]);
        var coordinator = new RetrievalCoordinator([], []);
        var preparer = new ChangePreparer(TimeProvider.System);
        var freshness = new IndexFreshness(null);
        return new WriteRouter(
            configuration, catalog,
            new MaintenanceOperation(configuration, catalog, coordinator, preparer, null!, freshness),
            new ProposalOperation(catalog, preparer, new EmptyStore(), null!, freshness, TimeProvider.System),
            new PublishOperation(catalog, preparer, null!, null!, freshness, TimeProvider.System),
            [host]);
    }

    private static NewEntry Entry(EntryKind kind) => new(kind, "Title", "Summary.", "Body.");

    [Fact]
    public async Task Knowledge_goes_to_the_knowledge_collection_and_is_written_where_the_key_may_maintain()
    {
        var host = new FakeHost("personal", Remote("library", Capability.Read | Capability.Maintain));
        var saved = await Router(host).CreateAsync(Caller, null, Entry(EntryKind.Knowledge), null, [], CancellationToken.None);

        Assert.True(saved.IsSuccess, saved.Error?.Message);
        Assert.NotNull(saved.Value.Written);
        Assert.Equal(["create library Knowledge"], host.Calls);
    }

    [Fact]
    public async Task A_propose_only_target_turns_the_save_into_a_proposal()
    {
        var host = new FakeHost("personal", Remote("library", Capability.Read | Capability.Propose));
        var router = Router(host);

        var created = await router.CreateAsync(Caller, null, Entry(EntryKind.Knowledge), null, [], CancellationToken.None);
        var updated = await router.ChangeAsync(Caller, MaintenanceAction.Update, EntryRef.Parse("library/x"), Base, new EntryPatch { Summary = "New." }, null, [], CancellationToken.None);

        Assert.Equal("personal:p-1", created.Value.Proposed?.QualifiedId);
        Assert.NotNull(updated.Value.Proposed);
        Assert.Equal(["propose Create library", "propose Update library"], host.Calls);
    }

    [Fact]
    public async Task Memories_never_go_to_the_knowledge_collection()
    {
        var host = new FakeHost("personal", Remote("library", Capability.Read | Capability.Maintain));
        var saved = await Router(host).CreateAsync(Caller, null, Entry(EntryKind.Memory), null, [], CancellationToken.None);

        Assert.Equal(ErrorCode.ValidationFailed, saved.Error?.Code);
        Assert.Contains("writes.memory", saved.Error!.Message, StringComparison.Ordinal);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task Read_only_unknown_and_unwritable_targets_are_refused_without_writing()
    {
        var host = new FakeHost("personal", Remote("docs", Capability.Read, readOnly: true), Remote("shared", Capability.Read));
        var router = Router(host);

        Assert.Equal(ErrorCode.ReadOnly, (await router.CreateAsync(Caller, CollectionId.Parse("docs"), Entry(EntryKind.Knowledge), null, [], CancellationToken.None)).Error?.Code);
        Assert.Equal(ErrorCode.Forbidden, (await router.CreateAsync(Caller, CollectionId.Parse("shared"), Entry(EntryKind.Knowledge), null, [], CancellationToken.None)).Error?.Code);
        Assert.Equal(ErrorCode.NotFound, (await router.CreateAsync(Caller, CollectionId.Parse("elsewhere"), Entry(EntryKind.Knowledge), null, [], CancellationToken.None)).Error?.Code);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task Lifecycle_changes_and_deletes_reach_the_host_with_their_action()
    {
        var host = new FakeHost("personal", Remote("library", Capability.Read | Capability.Maintain));
        var router = Router(host);
        var entry = EntryRef.Parse("library/x");

        await router.ChangeAsync(Caller, MaintenanceAction.Archive, entry, Base, new EntryPatch { Archive = true }, null, [], CancellationToken.None);
        await router.ChangeAsync(Caller, MaintenanceAction.Supersede, entry, Base, new EntryPatch { SupersededBy = EntryRef.Parse("library/y") }, null, [], CancellationToken.None);
        await router.DeleteAsync(Caller, entry, Base, null, CancellationToken.None);

        Assert.Equal(["Archive library/x", "Supersede library/x", "delete library/x"], host.Calls);
    }

    [Fact]
    public async Task Proposals_are_listed_across_hosts_and_decided_on_the_host_named_in_the_id()
    {
        var up = new FakeHost("personal", Remote("library", Capability.Read | Capability.Publish));
        var listed = await Router(up).ListProposalsAsync(null, CancellationToken.None);
        Assert.Equal(["personal:p-1"], listed.Proposals.Select(p => p.QualifiedId));

        var accepted = await Router(up).DecideAsync(Caller, "personal:p-1", accept: true, null, CancellationToken.None);
        Assert.Equal(ProposalStatus.Accepted, accepted.Value.Status);
        Assert.Equal(["accept p-1"], up.Calls);

        Assert.Equal(ErrorCode.NotFound, (await Router(up).DecideAsync(Caller, "work:p-1", accept: true, null, CancellationToken.None)).Error?.Code);

        var down = new FakeHost("personal") { ListFails = true };
        var partial = await Router(down).ListProposalsAsync(null, CancellationToken.None);
        Assert.Single(partial.Unavailable);
    }
}
