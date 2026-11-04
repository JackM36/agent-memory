using System.Text;
using AgentBrain.Application.Abstractions.Indexing;
using AgentBrain.Application.Abstractions.Storage;
using AgentBrain.Application.Retrieval;
using AgentBrain.Application.Retrieval.QueryAnalysis;
using AgentBrain.Domain.Entries;
using AgentBrain.Domain.Identity;
using AgentBrain.Domain.Revisions;

namespace AgentBrain.Infrastructure.Sqlite.Tests;

/// <summary>Random hostile query text never reaches FTS syntax: analysis either rejects it or the query runs (plan §9.7).</summary>
public sealed class FtsFuzzTests
{
    private static readonly string[] Fragments =
    [
        "\"", "'", "*", "^", ":", "(", ")", "{", "}", "[", "]", "-", "+", "NEAR", "NEAR/2", "AND", "OR", "NOT", "title:", "{title body}:",
        "\\", ";", "--", "/*", "*/", "DROP TABLE entries", "%", "_", "\0", "‮", "ﬁ", "日本", "🙂", "0x41", " ", "\t", "\n", "rowid", "MATCH",
    ];

    [Fact]
    public async Task Ten_thousand_random_queries_never_break_the_index()
    {
        using var index = SqliteSearchIndex.Open(":memory:");
        var collection = CollectionId.Parse("kb");
        var metadata = new EntryMetadata { Id = EntryId.Parse("entry"), Kind = EntryKind.Knowledge, Title = "Retry policy", Summary = "Retry with backoff and jitter." };
        var applicability = new Applicability(ApplicabilityScope.General, [], [], [], []);
        var stored = new StoredEntry(collection, metadata, applicability, "Body text.", RevisionCalculator.Compute(metadata, "Body text."), "entry.md");
        await index.ApplyAsync(new IndexChanges(collection, [new ScannedFile(new FileStamp("entry.md", 10, 1), "h", stored, null, [])], []), TestContext.Current.CancellationToken);

        var random = new Random(20261102);
        for (var i = 0; i < 10_000; i++)
        {
            var text = new StringBuilder();
            for (var n = random.Next(1, 12); n > 0; n--)
            {
                text.Append(Fragments[random.Next(Fragments.Length)]);
                if (random.Next(3) == 0)
                {
                    text.Append("retry");
                }
            }

            if (!QueryAnalyzer.TryAnalyze(text.ToString(), [], out var terms, out _))
            {
                continue;
            }

            var set = await index.FindCandidatesAsync(
                new CandidateQuery(terms, QueryContext.Empty, [collection], [], SearchFilters.None, InclusionOptions.Default, 10), TestContext.Current.CancellationToken);
            Assert.True(set.Candidates.Count <= 1);
        }

        Assert.Equal(1, (await index.GetStatusAsync(TestContext.Current.CancellationToken)).Entries);
    }
}
