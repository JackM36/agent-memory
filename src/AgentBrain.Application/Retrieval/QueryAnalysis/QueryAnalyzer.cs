using System.Text.RegularExpressions;
using AgentBrain.Application.Abstractions.Indexing;
using AgentBrain.Domain.Entries;

namespace AgentBrain.Application.Retrieval.QueryAnalysis;

/// <summary>
/// Turns query text into concepts (plan §5.7). Each word is one concept; stopwords are dropped;
/// identifiers also match their split words; alias groups add equivalent terms to the concept, never
/// new concepts, so expansion can make an entry match but never makes it look more relevant.
/// Raw text never reaches the index; the index quotes every term.
/// </summary>
public static partial class QueryAnalyzer
{
    public const int MaxQueryLength = 1000;
    public const int MaxConcepts = 32;

    // Function words plus task filler that agents put around the real subject ("try again", "after", "make").
    // Kept small: a domain word wrongly listed here can never be found.
    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "a", "about", "after", "again", "all", "also", "an", "and", "any", "are", "as", "at", "be", "before",
        "but", "by", "can", "could", "do", "does", "doing", "for", "from", "get", "gets", "getting", "has", "have",
        "how", "i", "if", "in", "into", "is", "it", "its", "just", "make", "makes", "me", "my", "need", "needs",
        "of", "on", "or", "our", "should", "so", "some", "that", "the", "their", "them", "then", "there", "these",
        "they", "this", "those", "to", "try", "trying", "us", "use", "using", "want", "was", "way", "we", "were",
        "what", "when", "where", "which", "while", "why", "will", "with", "would", "you", "your",
    };

    public static bool TryAnalyze(string query, IReadOnlyList<AliasGroup> aliases, out QueryTerms terms, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(aliases);
        terms = QueryTerms.Empty;
        problem = null;
        if (query.Length > MaxQueryLength)
        {
            problem = $"The query is {query.Length} characters; the limit is {MaxQueryLength}.";
            return false;
        }

        var words = Words().Matches(query).Select(m => m.Value.Trim('.', '_', '-')).Where(w => w.Length > 0).ToList();
        var concepts = new List<QueryConcept>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var consumed = new bool[words.Count];

        for (var i = 0; i < words.Count; i++)
        {
            if (consumed[i])
            {
                continue;
            }

            // Multi-word alias terms ("entity component system") are matched as one concept.
            var (aliasGroup, length) = MatchAlias(words, i, aliases);
            string original;
            var equivalents = new List<string>();
            if (aliasGroup is not null)
            {
                original = string.Join(' ', words.Skip(i).Take(length)).ToLowerInvariant();
                equivalents.AddRange(aliasGroup.Terms);
                for (var j = i; j < i + length; j++)
                {
                    consumed[j] = true;
                }
            }
            else
            {
                var word = words[i];
                original = word.ToLowerInvariant();
                if (Stopwords.Contains(original))
                {
                    continue;
                }

                equivalents.Add(original);
                var parts = IdentifierSplitter.Split(word);
                if (parts.Count > 1)
                {
                    equivalents.Add(string.Join(' ', parts));
                    equivalents.Add(string.Concat(parts));
                }
            }

            if (!seen.Add(original))
            {
                continue;
            }

            equivalents.Insert(0, original);
            concepts.Add(new QueryConcept(original, [.. equivalents.Distinct(StringComparer.Ordinal)]));
        }

        if (concepts.Count > MaxConcepts)
        {
            problem = $"The query has {concepts.Count} distinct terms; the limit is {MaxConcepts}.";
            return false;
        }

        terms = new QueryTerms(concepts);
        return true;
    }

    /// <summary>True when the query mentions one of <paramref name="names"/> (used for the technology-mismatch exception).</summary>
    public static bool Mentions(QueryTerms terms, IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(terms);
        var set = names.ToHashSet(StringComparer.Ordinal);
        return terms.Concepts.Any(c => c.Terms.Any(set.Contains));
    }

    private static (AliasGroup? Group, int Length) MatchAlias(List<string> words, int start, IReadOnlyList<AliasGroup> aliases)
    {
        AliasGroup? best = null;
        var bestLength = 0;
        foreach (var group in aliases)
        {
            foreach (var term in group.Terms)
            {
                var parts = term.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length <= bestLength || start + parts.Length > words.Count)
                {
                    continue;
                }

                var matches = true;
                for (var k = 0; k < parts.Length && matches; k++)
                {
                    matches = string.Equals(words[start + k], parts[k], StringComparison.OrdinalIgnoreCase);
                }

                if (matches)
                {
                    best = group;
                    bestLength = parts.Length;
                }
            }
        }

        return (best, bestLength);
    }

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}_.#+-]*", RegexOptions.CultureInvariant)]
    private static partial Regex Words();
}
