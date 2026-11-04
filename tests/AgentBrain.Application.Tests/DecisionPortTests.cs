using AgentBrain.Application.Abstractions.Indexing;
using AgentBrain.Application.Capture;
using AgentBrain.Application.Decisions;
using AgentBrain.Application.Retrieval;
using AgentBrain.Application.Retrieval.QueryAnalysis;
using AgentBrain.Application.Retrieval.Ranking;
using AgentBrain.Domain.Entries;
using AgentBrain.Domain.Identity;
using AgentBrain.Domain.Revisions;

namespace AgentBrain.Application.Tests;

/// <summary>The heuristic ports are the behaviour that existed before ADR 0010, unchanged.</summary>
public sealed class DecisionPortTests
{
    private static readonly Revision Rev = Revision.Parse("r1-" + new string('a', 26));
    private static readonly QueryContext Game = new("space-game", ["unity"]);

    private static IndexCandidate Candidate(string reference, ApplicabilityScope scope, string[]? projects, int rank, (int Concept, bool Descriptive)[] hits, bool blocking = false)
    {
        var entryRef = EntryRef.Parse(reference);
        var metadata = new EntryMetadata
        {
            Id = entryRef.Entry,
            Kind = blocking ? EntryKind.Uncertainty : EntryKind.Knowledge,
            Title = "T",
            Summary = "S",
            Resolution = blocking ? Resolution.Unresolved : null,
            Blocking = blocking ? true : null,
        };
        return new IndexCandidate(
            new IndexedEntry(entryRef, metadata, new Applicability(scope, projects ?? [], [], [], []), Rev, 100, "x.md"),
            rank,
            [.. hits.Select(h => new ConceptHit(h.Concept, h.Descriptive))],
            false);
    }

    private static CandidateSet Mixed() => new(
        [
            Candidate("general.engineering/a", ApplicabilityScope.General, null, 1, [(0, true), (1, true)]),
            Candidate("space-game.memory/b", ApplicabilityScope.Project, ["space-game"], 2, [(0, true), (1, false)]),
            Candidate("web-shop.memory/c", ApplicabilityScope.Project, ["web-shop"], 3, [(0, false)]),
            Candidate("general.engineering/d", ApplicabilityScope.General, null, 4, [(1, false)]),
            Candidate("space-game.memory/q", ApplicabilityScope.Project, ["space-game"], 5, [(0, true), (1, true)], blocking: true),
            Candidate("space-game.memory/r", ApplicabilityScope.Project, ["space-game"], 6, [], blocking: true),
        ],
        [2, 2],
        50);

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public async Task The_heuristic_judge_returns_exactly_what_the_ranker_returns(double relaxation)
    {
        QueryAnalyzer.TryAnalyze("save system corruption", [], out var terms, out _);
        var relaxed = Relaxation.FromValue(relaxation);
        var set = Mixed();

        var (results, blocking) = Ranker.Rank(set, terms, Game, relaxed, explain: true);
        var judged = await HeuristicRelevanceJudge.Instance.JudgeAsync(new RelevanceInput("save system corruption", set, terms, Game, relaxed, true), CancellationToken.None);

        Assert.Equal(results.Select(r => (r.Candidate.Entry.Ref, r.Score, r.Explanation)), judged.Results.Select(r => (r.Candidate.Entry.Ref, r.Score, r.Explanation)));
        Assert.Equal(blocking.Select(r => r.Candidate.Entry.Ref), judged.Blocking.Select(r => r.Candidate.Entry.Ref));
        Assert.Empty(judged.Notes);
    }

    [Fact]
    public void Evaluate_reports_every_candidate_and_agrees_with_rank_on_admission()
    {
        QueryAnalyzer.TryAnalyze("save system corruption", [], out var terms, out _);
        var set = Mixed();

        var evaluated = Ranker.Evaluate(set, terms, Game, Relaxation.Default, explain: false);
        var (results, blocking) = Ranker.Rank(set, terms, Game, Relaxation.Default, explain: false);

        Assert.Equal(set.Candidates.Count, evaluated.Count);
        Assert.Equal(
            results.Select(r => r.Candidate.Entry.Ref.ToString()).Order(StringComparer.Ordinal),
            evaluated.Where(e => e.Admitted && !e.IsBlocking).Select(e => e.Scored.Candidate.Entry.Ref.ToString()).Order(StringComparer.Ordinal));
        Assert.Equal(
            blocking.Select(r => r.Candidate.Entry.Ref.ToString()).Order(StringComparer.Ordinal),
            evaluated.Where(e => e.Admitted && e.IsBlocking).Select(e => e.Scored.Candidate.Entry.Ref.ToString()).Order(StringComparer.Ordinal));
        Assert.Contains(evaluated, e => !e.Admitted && !e.IsBlocking);
    }

    [Fact]
    public async Task The_heuristic_classifier_never_suggests_so_the_agents_choice_stands()
    {
        var result = await HeuristicEntryClassifier.Instance.ClassifyAsync(
            new ClassificationInput(CollectionId.Parse("space-game.memory"), EntryKind.Memory, "Saves", "How saves work", [], null), CancellationToken.None);

        Assert.Null(result.Suggestion);
        Assert.Empty(result.Notes);
    }

    [Fact]
    public async Task The_heuristic_duplicate_judge_keeps_the_lexical_list_unchanged()
    {
        var similar = new List<SimilarEntry> { new(EntryRef.Parse("space-game.memory/a"), "A", "About a") };

        var result = await HeuristicDuplicateJudge.Instance.JudgeAsync(
            new DuplicateInput(CollectionId.Parse("space-game.memory"), "T", "S", [], similar), CancellationToken.None);

        Assert.Equal(similar, result.Similar);
        Assert.Null(result.Similar[0].Verdict);
    }

    [Fact]
    public async Task The_heuristic_gate_always_says_remember_without_judging()
    {
        var decision = await HeuristicCaptureGate.Instance.EvaluateAsync(new CaptureCandidate(null, "anything"), CancellationToken.None);

        Assert.True(decision.Remember);
        Assert.Null(decision.Likelihood);
        Assert.Equal("none", decision.Source);
    }

    [Fact]
    public void A_classification_only_disagrees_when_it_differs_and_is_confident_enough()
    {
        var suggestion = new Classification(EntryKind.Knowledge, 0.91, true, "jev");

        Assert.Contains("'knowledge' instead of 'memory'", suggestion.Disagreement(EntryKind.Memory, 0.6));
        Assert.Null(suggestion.Disagreement(EntryKind.Knowledge, 0.6));
        Assert.Null(suggestion.Disagreement(EntryKind.Memory, 0.95));
    }

    [Fact]
    public void Heuristic_services_use_no_model()
    {
        Assert.False(DecisionServices.Heuristic.Settings.UsesModel);
        Assert.Empty(DecisionServices.Heuristic.Problems);
    }

    [Fact]
    public void The_meter_counts_calls_tokens_cache_hits_and_failures()
    {
        var meter = new DecisionMeter();

        meter.RecordCall(1000, fromCache: false);
        meter.RecordCall(500, fromCache: false);
        meter.RecordCall(0, fromCache: true);
        meter.RecordFailure();

        Assert.Equal(2, meter.Calls);
        Assert.Equal(1500, meter.InputTokens);
        Assert.Equal(1, meter.CacheHits);
        Assert.Equal(1, meter.Failures);
        Assert.Equal(1500 * DecisionMeter.DollarsPerInputToken, meter.EstimatedDollars, 12);
    }
}
