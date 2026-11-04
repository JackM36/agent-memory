using AgentBrain.Application.Abstractions.Providers;
using AgentBrain.Application.Common;
using AgentBrain.Application.Retrieval;
using AgentBrain.Application.Retrieval.Ranking;
using AgentBrain.Domain.Access;
using AgentBrain.Domain.Entries;
using AgentBrain.Domain.Identity;
using AgentBrain.Domain.Revisions;

namespace AgentBrain.Application.Tests;

public sealed class CoordinatorTests
{
    private static readonly SearchRequest Request = new(
        "q", QueryContext.Empty, SearchFilters.None, Relaxation.Default, new ResponseLimits(10, 4000), InclusionOptions.Default, 20);

    private static RankedEntry Entry(string reference, int rank, string source) => new(
        new EntrySummary(EntryRef.Parse(reference), EntryKind.Knowledge, "T", "S", [], new Applicability(ApplicabilityScope.General, [], [], [], []),
            ProvenanceSource.Curated, 10, Revision.Parse("r1-" + new string('b', 26)), null),
        rank, ApplicabilityClass.General, source);

    private sealed class FakeProvider(string id, Func<CancellationToken, Task<Result<ProviderSearchResult>>> search) : IKnowledgeProvider
    {
        public string SourceId => id;

        public IReadOnlyList<CollectionInfo> Collections { get; } = [new(CollectionId.Parse(id), id, Capability.Read, true, id)];

        public async ValueTask<Result<ProviderSearchResult>> SearchAsync(SearchRequest request, CancellationToken cancellationToken) => await search(cancellationToken);

        public ValueTask<Result<Page<RankedEntry>>> ListAsync(ListRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<Result<EntryContent>> GetAsync(GetRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<Result<IReadOnlyList<OutgoingReference>>> ReferencesAsync(EntryRef entry, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<Result<Page<Backlink>>> BacklinksAsync(EntryRef entry, string? cursor, int pageSize, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static FakeProvider Returning(string id, params RankedEntry[] results) =>
        new(id, _ => Task.FromResult<Result<ProviderSearchResult>>(new ProviderSearchResult(results, [], false)));

    [Fact]
    public async Task A_slow_source_times_out_and_the_rest_is_returned_as_partial()
    {
        var slow = new FakeProvider("remote", async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new ProviderSearchResult([], [], false);
        });
        var coordinator = new RetrievalCoordinator([(Returning("local", Entry("kb/a", 1, "local")), null), (slow, TimeSpan.FromMilliseconds(50))]);

        var outcome = await coordinator.SearchAsync(Request, false, TestContext.Current.CancellationToken);

        Assert.True(outcome.IsPartial);
        Assert.Equal(SourceState.Timeout, outcome.Sources.Single(s => s.SourceId == "remote").State);
        Assert.Single(outcome.Results);
    }

    [Fact]
    public async Task An_empty_successful_search_is_not_partial()
    {
        var coordinator = new RetrievalCoordinator([(Returning("local"), null)]);
        var outcome = await coordinator.SearchAsync(Request, false, TestContext.Current.CancellationToken);
        Assert.False(outcome.IsPartial);
        Assert.Empty(outcome.Results);
    }

    [Fact]
    public async Task Duplicate_identities_count_once_at_their_best_rank()
    {
        var coordinator = new RetrievalCoordinator(
        [
            (Returning("one", Entry("kb/a", 1, "one"), Entry("kb/b", 2, "one")), null),
            (Returning("two", Entry("kb/b", 1, "two"), Entry("kb/c", 2, "two")), null),
        ]);

        var outcome = await coordinator.SearchAsync(Request, false, TestContext.Current.CancellationToken);

        Assert.Equal(["kb/a", "kb/b", "kb/c"], outcome.Results.Select(r => r.Entry.Ref.ToString()));
        Assert.Equal([1, 2, 3], outcome.Results.Select(r => r.Rank));
    }

    [Fact]
    public async Task A_failing_source_is_reported_with_its_state()
    {
        var failing = new FakeProvider("remote", _ => Task.FromResult<Result<ProviderSearchResult>>(new Error(ErrorCode.Unauthorized, "bad key")));
        var coordinator = new RetrievalCoordinator([(failing, null)]);

        var outcome = await coordinator.SearchAsync(Request, false, TestContext.Current.CancellationToken);

        Assert.Equal(SourceState.Unauthorized, Assert.Single(outcome.Sources).State);
    }
}
