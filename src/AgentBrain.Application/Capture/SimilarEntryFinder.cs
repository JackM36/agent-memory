using AgentBrain.Application.Abstractions.Indexing;
using AgentBrain.Application.Retrieval;
using AgentBrain.Application.Retrieval.QueryAnalysis;
using AgentBrain.Application.Retrieval.Ranking;
using AgentBrain.Domain.Entries;
using AgentBrain.Domain.Identity;

namespace AgentBrain.Application.Capture;

/// <summary>An existing entry that may already cover what is about to be written.</summary>
public sealed record SimilarEntry(EntryRef Ref, string Title, string Summary);

/// <summary>
/// Likely-duplicate check before writing (plan §8.3): broad-admission search over the target collection
/// using the new entry's title, summary and tags. It informs; it never blocks a write.
/// </summary>
public sealed class SimilarEntryFinder(ISearchIndex? index, IReadOnlyList<AliasGroup> aliases)
{
    public const int MaxResults = 5;

    public async ValueTask<IReadOnlyList<SimilarEntry>> FindAsync(CollectionId collection, string title, string summary, IEnumerable<string> tags, EntryId? exclude, CancellationToken cancellationToken)
    {
        if (index is null)
        {
            return [];
        }

        var text = string.Join(' ', new[] { title, summary }.Concat(tags));
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(QueryAnalyzer.MaxConcepts * 2);
        if (!QueryAnalyzer.TryAnalyze(string.Join(' ', words), aliases, out var terms, out _))
        {
            return [];
        }

        if (terms.Concepts.Count > QueryAnalyzer.MaxConcepts)
        {
            terms = new QueryTerms([.. terms.Concepts.Take(QueryAnalyzer.MaxConcepts)]);
        }

        var set = await index.FindCandidatesAsync(
            new CandidateQuery(terms, QueryContext.Empty, [collection], [], SearchFilters.None, InclusionOptions.Default, 20), cancellationToken);
        var (results, _) = Ranker.Rank(set, terms, QueryContext.Empty, Relaxation.FromValue(0.5), explain: false);
        return
        [
            .. results
                .Where(r => r.Candidate.Entry.Ref.Entry != exclude)
                .Take(MaxResults)
                .Select(r => new SimilarEntry(r.Candidate.Entry.Ref, r.Candidate.Entry.Metadata.Title, r.Candidate.Entry.Metadata.Summary)),
        ];
    }
}
