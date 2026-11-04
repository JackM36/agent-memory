using AgentBrain.Application.Abstractions.Providers;
using AgentBrain.Application.Common;
using AgentBrain.Application.Retrieval;
using AgentBrain.Domain.Entries;
using AgentBrain.Domain.Identity;

namespace AgentBrain.Application.Operations.Discovery;

public sealed record ListInput
{
    public IReadOnlyList<EntryKind>? Kinds { get; init; }

    public IReadOnlyList<string>? Tags { get; init; }

    public TagMatch TagMatch { get; init; } = TagMatch.All;

    public IReadOnlyList<CollectionId>? Collections { get; init; }

    public InclusionOptions Include { get; init; } = InclusionOptions.Default;

    public bool BlockingOnly { get; init; }

    public string? Cursor { get; init; }

    public int PageSize { get; init; } = PageDefaults.PageSize;
}

/// <summary>
/// Exact listing with stable <c>(collection, entry)</c> ordering and pagination across every source (spec §7.7,
/// plan §5.10). No relevance cutoff: walking the pages enumerates every matching entry. The cursor carries each
/// source's position, so entries are never seen twice; a source that fails keeps its position and the page is
/// marked partial. There is no snapshot isolation: entries changed during traversal may be missed.
/// </summary>
public sealed class ListOperation(RetrievalCoordinator coordinator, IndexFreshness freshness)
{
    private const string Done = "!done";

    public IReadOnlyList<CollectionInfo> Collections() => coordinator.Collections;

    public async ValueTask<Result<ListResponse>> ExecuteAsync(ListInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var stale = await freshness.EnsureFreshAsync(cancellationToken);
        var positions = input.Cursor is null ? [] : DecodePositions(input.Cursor);
        if (positions is null)
        {
            return Error.Validation("The cursor is not valid for this listing.");
        }

        var pageSize = Math.Clamp(input.PageSize, 1, PageDefaults.MaxPageSize);
        var filters = new SearchFilters(input.Kinds, input.Tags, input.TagMatch, input.Collections);
        var fetched = new List<(IKnowledgeProvider Provider, Page<RankedEntry>? Page)>();
        var statuses = new List<SourceStatus>();

        foreach (var provider in coordinator.Providers)
        {
            if (positions.GetValueOrDefault(provider.SourceId) == Done)
            {
                continue;
            }

            if (input.Collections is { Count: > 0 } wanted && !provider.Collections.Any(c => wanted.Contains(c.Id)) && provider.Collections.Count > 0)
            {
                continue;
            }

            var after = positions.GetValueOrDefault(provider.SourceId);
            var request = new ListRequest(filters, input.Include, input.BlockingOnly, after is null ? null : Cursors.Encode(after), pageSize);
            var page = await provider.ListAsync(request, cancellationToken);
            fetched.Add((provider, page.IsSuccess ? page.Value : null));
            statuses.Add(page.IsSuccess ? new SourceStatus(provider.SourceId, SourceState.Ok) : new SourceStatus(provider.SourceId, SourceState.Unavailable, page.Error.Message));
        }

        var merged = fetched
            .Where(f => f.Page is not null)
            .SelectMany(f => f.Page!.Items.Select(item => (f.Provider, Item: item)))
            .OrderBy(x => x.Item.Entry.Ref.Collection.Value, StringComparer.Ordinal)
            .ThenBy(x => x.Item.Entry.Ref.Entry.Value, StringComparer.Ordinal)
            .ToList();

        var taken = new List<RankedEntry>();
        var seen = new HashSet<EntryRef>();
        EntryRef? last = null;
        foreach (var (_, item) in merged)
        {
            if (seen.Contains(item.Entry.Ref))
            {
                continue;
            }

            if (taken.Count == pageSize)
            {
                break;
            }

            seen.Add(item.Entry.Ref);
            taken.Add(item with { Rank = taken.Count + 1 });
            last = item.Entry.Ref;
        }

        var next = new Dictionary<string, string>(positions, StringComparer.Ordinal);
        foreach (var (provider, page) in fetched)
        {
            if (page is null)
            {
                continue;
            }

            var consumed = page.Items.Where(i => last is not null && Compare(i.Entry.Ref, last) <= 0).ToList();
            var all = consumed.Count == page.Items.Count;
            if (all && page.NextCursor is null)
            {
                next[provider.SourceId] = Done;
            }
            else if (consumed.Count > 0)
            {
                next[provider.SourceId] = consumed[^1].Entry.Ref.ToString();
            }
        }

        var more = coordinator.Providers.Any(p => next.GetValueOrDefault(p.SourceId) != Done);
        return new ListResponse(taken, more ? EncodePositions(next) : null, statuses, stale);
    }

    private static int Compare(EntryRef a, EntryRef b)
    {
        var byCollection = string.CompareOrdinal(a.Collection.Value, b.Collection.Value);
        return byCollection != 0 ? byCollection : string.CompareOrdinal(a.Entry.Value, b.Entry.Value);
    }

    private static string EncodePositions(Dictionary<string, string> positions) =>
        Cursors.Encode(string.Join('\n', positions.Select(p => p.Key + "\t" + p.Value)));

    private static Dictionary<string, string>? DecodePositions(string cursor)
    {
        var text = Cursors.Decode(cursor);
        if (text is null)
        {
            return null;
        }

        var positions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = line.IndexOf('\t', StringComparison.Ordinal);
            if (tab <= 0 || (line[(tab + 1)..] != Done && !EntryRef.TryParse(line[(tab + 1)..], out _)))
            {
                return null;
            }

            positions[line[..tab]] = line[(tab + 1)..];
        }

        return positions;
    }
}
