using AgentBrain.Application.Common;
using AgentBrain.Application.Retrieval;
using AgentBrain.Domain.Identity;

namespace AgentBrain.Application.Abstractions.Providers;

/// <summary>
/// Application-level retrieval against one source: the local collections or a remote host (spec §3.3,
/// abstraction 3). Each provider applies eligibility, admission and its own applicability ranking
/// before returning, so the coordinator never re-applies penalties (plan §6.5).
/// </summary>
public interface IKnowledgeProvider
{
    string SourceId { get; }

    /// <summary>The collections this source serves. Used to route get, list and relationship calls.</summary>
    IReadOnlyList<CollectionInfo> Collections { get; }

    ValueTask<Result<ProviderSearchResult>> SearchAsync(SearchRequest request, CancellationToken cancellationToken);

    /// <summary>Exhaustive listing in stable <c>(collection, entry)</c> order; <see cref="ListRequest.Cursor"/> is opaque.</summary>
    ValueTask<Result<Page<RankedEntry>>> ListAsync(ListRequest request, CancellationToken cancellationToken);

    ValueTask<Result<EntryContent>> GetAsync(GetRequest request, CancellationToken cancellationToken);

    ValueTask<Result<IReadOnlyList<OutgoingReference>>> ReferencesAsync(EntryRef entry, CancellationToken cancellationToken);

    ValueTask<Result<Page<Backlink>>> BacklinksAsync(EntryRef entry, string? cursor, int pageSize, CancellationToken cancellationToken);
}
