using AgentBrain.Application.Abstractions.Providers;
using AgentBrain.Application.Common;
using AgentBrain.Domain.Identity;

namespace AgentBrain.Application.Retrieval;

/// <summary>
/// Queries every source concurrently with its own timeout, keeps partial results when a source fails, and
/// merges results (plan §6.5–§6.6). Sources already applied admission and applicability, so merging only
/// interleaves by rank (rank fusion with one rank per identity). The same identity from several sources
/// counts once, at its best rank; differing revisions are reported as a conflict, never resolved silently.
/// </summary>
public sealed class RetrievalCoordinator
{
    private readonly IReadOnlyList<(IKnowledgeProvider Provider, TimeSpan? Timeout)> _sources;
    private readonly IReadOnlyList<string> _preference;

    public RetrievalCoordinator(IReadOnlyList<(IKnowledgeProvider Provider, TimeSpan? Timeout)> sources, IReadOnlyList<string>? preference = null)
    {
        _sources = sources;
        _preference = preference ?? [];
        Providers = [.. sources.Select(s => s.Provider)];
    }

    public IReadOnlyList<IKnowledgeProvider> Providers { get; }

    public IReadOnlyList<CollectionInfo> Collections => [.. Providers.SelectMany(p => p.Collections)];

    /// <summary>The preferred provider for a collection: source preference first, then local before remote.</summary>
    public IKnowledgeProvider? ProviderFor(CollectionId collection) => ProvidersFor(collection) is { Count: > 0 } providers ? providers[0] : null;

    /// <summary>Every provider serving a collection, in preference order (for fallback when one is unavailable).</summary>
    public IReadOnlyList<IKnowledgeProvider> ProvidersFor(CollectionId collection) =>
    [
        .. Providers
            .Where(p => p.Collections.Any(c => c.Id == collection))
            .OrderBy(p => PreferenceRank(p.SourceId, collection))
            .ThenBy(p => p.SourceId == LocalKnowledgeProvider.Id ? 0 : 1)
            .ThenBy(p => p.SourceId, StringComparer.Ordinal),
    ];

    public async ValueTask<SearchOutcome> SearchAsync(SearchRequest request, bool indexStale, CancellationToken cancellationToken)
    {
        var calls = _sources.Select(s => CallAsync(s.Provider, s.Timeout, ct => s.Provider.SearchAsync(request, ct), cancellationToken)).ToList();
        var outcomes = await Task.WhenAll(calls);

        var statuses = new List<SourceStatus>();
        var results = new List<RankedEntry>();
        var blocking = new List<RankedEntry>();
        foreach (var (provider, result, state, detail) in outcomes)
        {
            if (state != SourceState.Ok || result is null)
            {
                statuses.Add(new SourceStatus(provider.SourceId, state, detail));
                continue;
            }

            statuses.AddRange(provider.SourceId == LocalKnowledgeProvider.Id
                ? provider.Collections.Select(c => new SourceStatus(c.Id.Value, SourceState.Ok))
                : [new SourceStatus(provider.SourceId, SourceState.Ok)]);
            results.AddRange(result.Results);
            blocking.AddRange(result.Blocking);
            indexStale |= result.IndexStale;
        }

        return new SearchOutcome(Merge(results), Merge(blocking), statuses, indexStale);
    }

    /// <summary>Interleaves by source rank; ties go to the closer applicability class, then identity.</summary>
    private List<RankedEntry> Merge(List<RankedEntry> entries) =>
    [
        .. entries
            .GroupBy(e => e.Entry.Ref)
            .Select(Collapse)
            .OrderBy(e => e.Rank)
            .ThenBy(e => e.Applicability)
            .ThenBy(e => e.Entry.Ref.Collection.Value, StringComparer.Ordinal)
            .ThenBy(e => e.Entry.Ref.Entry.Value, StringComparer.Ordinal)
            .Select((e, i) => e with { Rank = i + 1 }),
    ];

    /// <summary>One result per identity: the preferred source's copy at the best rank, with any revision disagreement attached.</summary>
    private RankedEntry Collapse(IGrouping<EntryRef, RankedEntry> copies)
    {
        var bestRank = copies.Min(c => c.Rank);
        var chosen = copies.OrderBy(c => PreferenceRank(c.SourceId, c.Entry.Ref.Collection)).ThenBy(c => c.Rank).First();
        var revisions = copies.Select(c => c.Entry.Revision).Distinct().ToList();
        var conflict = revisions.Count > 1
            ? new RevisionConflict([.. copies.Select(c => c.SourceId).Distinct()], revisions)
            : null;
        return chosen with { Rank = bestRank, Conflict = conflict };
    }

    private int PreferenceRank(string sourceId, CollectionId collection)
    {
        var bySource = IndexOf(sourceId);
        var byCollection = sourceId == LocalKnowledgeProvider.Id ? IndexOf(collection.Value) : int.MaxValue;
        return Math.Min(bySource, byCollection);
    }

    private int IndexOf(string name)
    {
        for (var i = 0; i < _preference.Count; i++)
        {
            if (_preference[i] == name)
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    private static async Task<(IKnowledgeProvider Provider, ProviderSearchResult? Result, SourceState State, string? Detail)> CallAsync(
        IKnowledgeProvider provider,
        TimeSpan? timeout,
        Func<CancellationToken, ValueTask<Result<ProviderSearchResult>>> call,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is { } limit)
        {
            linked.CancelAfter(limit);
        }

        try
        {
            var result = await call(linked.Token);
            return result.IsSuccess
                ? (provider, result.Value, SourceState.Ok, null)
                : (provider, null, result.Error.Code == ErrorCode.Unauthorized ? SourceState.Unauthorized : SourceState.Unavailable, result.Error.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (provider, null, SourceState.Timeout, null);
        }
    }
}
