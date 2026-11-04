using AgentBrain.Application.Abstractions.Proposals;
using AgentBrain.Application.Common;
using AgentBrain.Application.Configuration;
using AgentBrain.Application.Operations.Proposals;
using AgentBrain.Application.Operations.Routing;
using AgentBrain.Application.Writing;
using AgentBrain.Domain.Access;
using AgentBrain.Domain.Entries;
using AgentBrain.Domain.Identity;
using AgentBrain.Infrastructure.Http;

namespace AgentBrain.Server.Tests;

/// <summary>The client's remote writer against a real host: the host applies its own rules and the client maps the outcomes back.</summary>
public sealed class RemoteWriteTests : IAsyncLifetime
{
    private static readonly CollectionId Team = CollectionId.Parse("team");
    private TestHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await TestHost.StartAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private async Task<RemoteHttpProvider> ProviderAsync(params string[] scopes)
    {
        var connection = new EffectiveConnection("personal", new Uri("http://localhost/"), new("KEY"), [], TimeSpan.FromSeconds(5), ForwardPolicy.Nothing);
        return new RemoteHttpProvider(connection, await _host.KeyAsync(scopes), "space-game", _host.Handler);
    }

    private static ChangeRequest Create(string title, string summary, string body = "Body.") =>
        new(ChangeKind.Create, Team) { NewEntry = new NewEntry(EntryKind.Knowledge, title, summary, body) { Applies = new ApplicabilityOverrides(Technologies: ["unity"]) } };

    [Fact]
    public async Task A_maintain_key_writes_directly_and_conflicts_come_back_as_conflicts()
    {
        using var remote = await ProviderAsync("collection:team:read", "collection:team:maintain");
        var ct = TestContext.Current.CancellationToken;

        var info = await remote.DescribeAsync(Team, ct);
        Assert.True(info.IsSuccess, info.Error?.Message);
        Assert.True(info.Value.Capabilities.HasFlag(Capability.Maintain));
        Assert.Equal("personal", info.Value.SourceId);

        var created = await remote.CreateAsync(Create("Retry policy", "Retry idempotent calls with backoff."), [], ct);
        Assert.True(created.IsSuccess, created.Error?.Message);
        Assert.Equal("team/retry-policy", created.Value.Ref.ToString());

        var fetched = await remote.GetAsync(new Application.Retrieval.GetRequest(created.Value.Ref, 4000, false), ct);
        Assert.Contains("unity", fetched.Value.Entry.Applicability.Technologies);

        var update = new ChangeRequest(ChangeKind.Update, Team) { Entry = created.Value.Ref.Entry, BaseRevision = created.Value.Revision, Patch = new EntryPatch { Summary = "Retry idempotent calls only." } };
        var updated = await remote.MaintainAsync(MaintenanceAction.Update, update, [], ct);
        Assert.True(updated.IsSuccess, updated.Error?.Message);

        var stale = await remote.MaintainAsync(MaintenanceAction.Update, update, [], ct);
        Assert.Equal(ErrorCode.RevisionConflict, stale.Error?.Code);

        var archive = new ChangeRequest(ChangeKind.Update, Team) { Entry = created.Value.Ref.Entry, BaseRevision = updated.Value.Revision, Patch = new EntryPatch { Archive = true } };
        var archived = await remote.MaintainAsync(MaintenanceAction.Archive, archive, [], ct);
        Assert.True(archived.IsSuccess, archived.Error?.Message);

        var deleted = await remote.DeleteAsync(created.Value.Ref, archived.Value.Revision!, ct);
        Assert.True(deleted.IsSuccess, deleted.Error?.Message);
    }

    [Fact]
    public async Task Secrets_are_rejected_by_the_host_unless_the_rule_is_allowed()
    {
        using var remote = await ProviderAsync("collection:team:read", "collection:team:maintain");
        var ct = TestContext.Current.CancellationToken;

        var refused = await remote.CreateAsync(Create("Keys", "Where keys live.", "AKIAIOSFODNN7EXAMPLE"), [], ct);
        Assert.Equal(ErrorCode.SecretDetected, refused.Error?.Code);
        var rule = refused.Error!.Details!.Keys.First();

        var allowed = await remote.CreateAsync(Create("Keys", "Where keys live.", "AKIAIOSFODNN7EXAMPLE"), [rule], ct);
        Assert.True(allowed.IsSuccess, allowed.Error?.Message);
    }

    [Fact]
    public async Task A_propose_key_proposes_and_a_publish_key_accepts_from_another_machine()
    {
        using var proposer = await ProviderAsync("collection:team:read", "collection:team:propose");
        using var reviewer = await ProviderAsync("collection:team:read", "collection:team:publish");
        var ct = TestContext.Current.CancellationToken;

        Assert.False((await proposer.DescribeAsync(Team, ct)).Value.Capabilities.HasFlag(Capability.Maintain));
        Assert.Equal(ErrorCode.Forbidden, (await proposer.CreateAsync(Create("Pooling", "Pool connections."), [], ct)).Error?.Code);

        var proposal = await proposer.ProposeAsync(Create("Pooling", "Pool connections."), "From an incident", [], ct);
        Assert.True(proposal.IsSuccess, proposal.Error?.Message);
        Assert.Equal("personal", proposal.Value.Source);
        Assert.StartsWith("personal:p-", proposal.Value.QualifiedId, StringComparison.Ordinal);

        var pending = await reviewer.ListProposalsAsync(ProposalStatus.Pending, ct);
        Assert.Contains(pending.Value, p => p.Id == proposal.Value.Id);
        Assert.Contains("Pool connections.", (await reviewer.ShowProposalAsync(proposal.Value.Id, ct)).Value.Summary, StringComparison.Ordinal);

        var accepted = await reviewer.DecideAsync(proposal.Value.Id, accept: true, "Agreed", ct);
        Assert.True(accepted.IsSuccess, accepted.Error?.Message);
        Assert.Equal(ProposalStatus.Accepted, accepted.Value.Status);
    }

    [Fact]
    public async Task Collections_the_key_cannot_read_are_not_found_and_read_only_ones_say_so()
    {
        using var remote = await ProviderAsync("collection:team:read");
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(ErrorCode.NotFound, (await remote.DescribeAsync(CollectionId.Parse("fast"), ct)).Error?.Code);

        using var notes = await ProviderAsync("collection:*:read", "collection:*:maintain");
        Assert.True((await notes.DescribeAsync(CollectionId.Parse("notes"), ct)).Value.ReadOnly);
    }
}
