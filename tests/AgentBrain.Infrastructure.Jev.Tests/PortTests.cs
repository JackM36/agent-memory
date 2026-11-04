using AgentBrain.Application.Abstractions.Indexing;
using AgentBrain.Application.Capture;
using AgentBrain.Application.Decisions;
using AgentBrain.Application.Retrieval;
using AgentBrain.Application.Retrieval.QueryAnalysis;
using AgentBrain.Application.Retrieval.Ranking;
using AgentBrain.Domain.Entries;
using AgentBrain.Domain.Identity;

namespace AgentBrain.Infrastructure.Jev.Tests;

public sealed class RelevanceTests
{
    private static RelevanceInput Input(CandidateSet set, QueryContext? context = null, bool explain = false)
    {
        QueryAnalyzer.TryAnalyze("save slots", [], out var terms, out _);
        return new RelevanceInput("save slots", set, terms, context ?? QueryContext.Empty, Relaxation.Default, explain);
    }

    /// <summary>Answers by the entry title found in the state.</summary>
    private static FakeModel ByTitle(Dictionary<string, double> probabilities, double applies = 1.0) => new(r =>
    {
        var title = probabilities.Keys.First(t => r.State.Contains("Entry title: " + t + "\n", StringComparison.Ordinal));
        return FakeModel.Noul(r.Questions.ContainsKey("applies") ? [("answers", probabilities[title]), ("applies", applies)] : [("answers", probabilities[title])]);
    });

    private static JevRelevanceJudge Judge(IDecisionModel model, DecisionMode mode, IExternalDecisionPolicy? policy = null, JevSettings? jev = null) =>
        new(model, Settings.Decisions(relevance: mode, jev: jev), policy ?? new AllowOnly("kb", "other"));

    private static string[] Titles(RelevanceResult result) => [.. result.Results.Select(r => r.Candidate.Entry.Metadata.Title)];

    [Fact]
    public async Task Jev_mode_decides_admission_alone()
    {
        var set = Candidates.Set(
            Candidates.Make("kb/good", "Good", hits: false, rank: 1),
            Candidates.Make("kb/bad", "Bad", hits: true, rank: 2));
        var model = ByTitle(new() { ["Good"] = 0.95, ["Bad"] = 0.10 });

        var result = await Judge(model, DecisionMode.Jev).JudgeAsync(Input(set), CancellationToken.None);

        Assert.Equal(["Good"], Titles(result));
        Assert.Empty(result.Notes);
    }

    [Fact]
    public async Task Hybrid_rescues_a_confident_entry_the_heuristic_rejected_and_vetoes_a_doubtful_one_it_admitted()
    {
        var set = Candidates.Set(
            Candidates.Make("kb/rescued", "Rescued", hits: false),
            Candidates.Make("kb/vetoed", "Vetoed", hits: true, rank: 1),
            Candidates.Make("kb/kept", "Kept", hits: true, rank: 2),
            Candidates.Make("kb/ignored", "Ignored", hits: false));
        var model = ByTitle(new() { ["Rescued"] = 0.85, ["Vetoed"] = 0.15, ["Kept"] = 0.5, ["Ignored"] = 0.5 });

        var result = await Judge(model, DecisionMode.Hybrid).JudgeAsync(Input(set), CancellationToken.None);

        Assert.Equal(["Rescued", "Kept"], Titles(result).OrderBy(t => t == "Kept" ? 1 : 0));
        Assert.DoesNotContain("Vetoed", Titles(result));
        Assert.DoesNotContain("Ignored", Titles(result));
    }

    [Fact]
    public async Task Another_projects_entry_needs_a_higher_probability_to_be_rescued()
    {
        var set = Candidates.Set(Candidates.Make("other/shop", "Shop", hits: false, scope: ApplicabilityScope.Project, projects: ["web-shop"]));
        var context = new QueryContext("space-game", ["unity"]);

        var at85 = await Judge(ByTitle(new() { ["Shop"] = 0.85 }), DecisionMode.Hybrid).JudgeAsync(Input(set, context), CancellationToken.None);
        var at95 = await Judge(ByTitle(new() { ["Shop"] = 0.95 }), DecisionMode.Hybrid).JudgeAsync(Input(set, context), CancellationToken.None);

        Assert.Empty(at85.Results);
        Assert.Single(at95.Results);
    }

    [Fact]
    public async Task A_low_applies_answer_caps_the_probability_and_the_question_is_only_asked_with_context()
    {
        var set = Candidates.Set(Candidates.Make("kb/x", "X", hits: false));
        var withContext = ByTitle(new() { ["X"] = 0.95 }, applies: 0.05);

        var none = await Judge(withContext, DecisionMode.Jev).JudgeAsync(Input(set, new QueryContext("p", ["unity"])), CancellationToken.None);

        Assert.Empty(none.Results);
        Assert.Contains("applies", withContext.Requests[0].Questions.Keys);

        var noContext = ByTitle(new() { ["X"] = 0.95 });
        var admitted = await Judge(noContext, DecisionMode.Jev).JudgeAsync(Input(set), CancellationToken.None);

        Assert.Single(admitted.Results);
        Assert.DoesNotContain("applies", noContext.Requests[0].Questions.Keys);
    }

    [Fact]
    public async Task Entries_of_collections_that_do_not_allow_external_decisions_are_never_sent_and_keep_the_heuristic_verdict()
    {
        var set = Candidates.Set(
            Candidates.Make("kb/open", "Open", hits: false),
            Candidates.Make("private/closed", "Closed", hits: true));
        var model = ByTitle(new() { ["Open"] = 0.95, ["Closed"] = 0.0 });

        var result = await Judge(model, DecisionMode.Jev, new AllowOnly("kb")).JudgeAsync(Input(set), CancellationToken.None);

        Assert.Single(model.Requests);
        Assert.DoesNotContain(model.Requests, r => r.State.Contains("Closed", StringComparison.Ordinal));
        Assert.Equal(["Open", "Closed"], Titles(result).Order(Comparer<string>.Create((a, b) => a == "Open" ? -1 : 1)));
    }

    [Fact]
    public async Task With_nothing_to_judge_the_model_is_never_contacted()
    {
        var set = Candidates.Set(Candidates.Make("private/closed", "Closed", hits: true));
        var model = FakeModel.AlwaysNoul(0.0);

        var result = await Judge(model, DecisionMode.Jev, ExternalDecisionPolicy.None).JudgeAsync(Input(set), CancellationToken.None);

        Assert.Equal(0, model.Calls);
        Assert.Equal(["Closed"], Titles(result));
        Assert.Equal(0, (await Judge(model, DecisionMode.Hybrid).JudgeAsync(Input(Candidates.Set()), CancellationToken.None)).Results.Count + model.Calls);
    }

    [Fact]
    public async Task The_pool_is_capped_at_the_smaller_of_the_pool_and_the_call_budget_best_heuristic_first()
    {
        var set = Candidates.Set(
            Candidates.Make("kb/a", "A", rank: 1),
            Candidates.Make("kb/b", "B", rank: 2),
            Candidates.Make("kb/c", "C", rank: 3),
            Candidates.Make("kb/d", "D", rank: 4),
            Candidates.Make("kb/e", "E", rank: 5));
        var model = FakeModel.AlwaysNoul(0.9);

        var result = await Judge(model, DecisionMode.Jev, jev: Settings.Jev(calls: 2, pool: 100)).JudgeAsync(Input(set), CancellationToken.None);

        Assert.Equal(2, model.Calls);
        Assert.Equal(2, model.Requests.Count(r => r.State.Contains("Entry title: A\n", StringComparison.Ordinal) || r.State.Contains("Entry title: B\n", StringComparison.Ordinal)));

        var pooled = FakeModel.AlwaysNoul(0.9);
        await Judge(pooled, DecisionMode.Jev, jev: Settings.Jev(calls: 100, pool: 3)).JudgeAsync(Input(set), CancellationToken.None);
        Assert.Equal(3, pooled.Calls);
        Assert.NotEmpty(result.Results);
    }

    [Fact]
    public async Task A_failing_model_falls_back_to_the_heuristic_with_one_note()
    {
        var set = Candidates.Set(
            Candidates.Make("kb/in", "In", hits: true),
            Candidates.Make("kb/out", "Out", hits: false));
        var heuristic = await HeuristicRelevanceJudge.Instance.JudgeAsync(Input(set), CancellationToken.None);

        var result = await Judge(new FakeModel(_ => FakeModel.Failure()), DecisionMode.Jev).JudgeAsync(Input(set), CancellationToken.None);

        Assert.Equal(Titles(heuristic), Titles(result));
        var note = Assert.Single(result.Notes);
        Assert.Contains("relevance judge unavailable", note, StringComparison.Ordinal);
        Assert.Contains("timed out", note, StringComparison.Ordinal);
        Assert.DoesNotContain("decision model unavailable:", note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Blocking_uncertainties_keep_the_heuristic_verdict_and_are_not_sent()
    {
        var set = Candidates.Set(
            Candidates.Make("kb/block", "Block", kind: EntryKind.Uncertainty, blocking: true),
            Candidates.Make("kb/plain", "Plain", hits: true));
        var model = ByTitle(new() { ["Plain"] = 0.9, ["Block"] = 0.0 });
        var heuristic = await HeuristicRelevanceJudge.Instance.JudgeAsync(Input(set), CancellationToken.None);

        var result = await Judge(model, DecisionMode.Jev).JudgeAsync(Input(set), CancellationToken.None);

        Assert.Single(model.Requests);
        Assert.Equal(heuristic.Blocking.Select(b => b.Candidate.Entry.Ref.ToString()), result.Blocking.Select(b => b.Candidate.Entry.Ref.ToString()));
        Assert.NotEmpty(result.Blocking);
        Assert.DoesNotContain("Block", Titles(result));
    }

    [Fact]
    public async Task The_state_has_the_task_and_the_entrys_description_but_the_explanation_shows_the_answer()
    {
        var set = Candidates.Set(Candidates.Make("kb/x", "X"));
        var model = ByTitle(new() { ["X"] = 0.834 });

        var result = await Judge(model, DecisionMode.Hybrid).JudgeAsync(Input(set, explain: true), CancellationToken.None);

        var state = model.Requests[0].State;
        Assert.Contains("Task: save slots", state, StringComparison.Ordinal);
        Assert.Contains("Entry summary: Summary of X", state, StringComparison.Ordinal);
        Assert.Contains("Entry tags: tag-a", state, StringComparison.Ordinal);
        Assert.EndsWith(" jev=0.83", result.Results[0].Explanation, StringComparison.Ordinal);
    }
}

public sealed class WritePortTests
{
    private static readonly CollectionId Mem = CollectionId.Parse("mem");

    private static FakeModel Choice(string name, string choice, double confidence, params (string Name, double Value)[] nouls) => new(_ =>
    {
        var answers = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal)
        {
            [name] = new ChoiceAnswer(choice, confidence, new Dictionary<string, double> { [choice] = confidence }),
        };
        foreach (var (n, v) in nouls)
        {
            answers[n] = new NoulAnswer(v);
        }

        return new DecisionResponse(answers, 7);
    });

    private static ClassificationInput Entry(CollectionId? destination) =>
        new(destination, EntryKind.Memory, "Retry with backoff", "Use exponential backoff", ["net"], new string('x', 2000));

    [Fact]
    public async Task The_classifier_suggests_a_kind_and_cuts_the_excerpt()
    {
        var model = Choice("kind", "knowledge", 0.92, ("none_fit", 0.1), ("technology_specific", 0.8));
        var classifier = new JevEntryClassifier(model, new AllowOnly("mem"));

        var result = await classifier.ClassifyAsync(Entry(Mem), CancellationToken.None);

        Assert.Equal(new Classification(EntryKind.Knowledge, 0.92, true, "jev"), result.Suggestion);
        Assert.Empty(result.Notes);
        var state = model.Requests[0].State;
        Assert.Contains("Kind chosen by the author: memory", state, StringComparison.Ordinal);
        Assert.Contains(new string('x', JevEntryClassifier.MaxExcerptChars), state, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', JevEntryClassifier.MaxExcerptChars + 1), state, StringComparison.Ordinal);
        Assert.Contains("knowledge", ((ChoiceQuestion)model.Requests[0].Questions["kind"]).Options.Select(o => o.Key));
        Assert.Contains("suggests 'knowledge' instead of 'memory'", result.Suggestion!.Disagreement(EntryKind.Memory, 0.6), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_classifier_makes_no_call_without_a_destination_that_allows_it()
    {
        var model = Choice("kind", "knowledge", 0.9, ("none_fit", 0), ("technology_specific", 0));

        Assert.Null((await new JevEntryClassifier(model, new AllowOnly("mem")).ClassifyAsync(Entry(null), CancellationToken.None)).Suggestion);
        Assert.Null((await new JevEntryClassifier(model, new AllowOnly("other")).ClassifyAsync(Entry(Mem), CancellationToken.None)).Suggestion);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task The_classifier_gives_no_suggestion_when_no_kind_fits_and_a_note_when_unavailable()
    {
        var none = Choice("kind", "note", 0.9, ("none_fit", 0.7), ("technology_specific", 0));
        Assert.Null((await new JevEntryClassifier(none, new AllowOnly("mem")).ClassifyAsync(Entry(Mem), CancellationToken.None)).Suggestion);

        var down = await new JevEntryClassifier(new FakeModel(_ => FakeModel.Failure()), new AllowOnly("mem")).ClassifyAsync(Entry(Mem), CancellationToken.None);
        Assert.Null(down.Suggestion);
        Assert.Contains("classifier unavailable", Assert.Single(down.Notes), StringComparison.Ordinal);
    }

    private static SimilarEntry Similar(string reference, string title) => new(EntryRef.Parse(reference), title, "Summary of " + title);

    private static DuplicateInput Duplicates(params SimilarEntry[] similar) => new(Mem, "New title", "New summary", ["t"], similar);

    private static FakeModel ByRelation(Dictionary<string, (string Relation, double Confidence)> byTitle) => new(r =>
    {
        var (relation, confidence) = byTitle.First(kv => r.State.Contains("Existing entry\nTitle: " + kv.Key + "\n", StringComparison.Ordinal)).Value;
        return new DecisionResponse(new Dictionary<string, DecisionAnswer>
        {
            ["relation"] = new ChoiceAnswer(relation, confidence, new Dictionary<string, double>()),
        }, 5);
    });

    [Fact]
    public async Task Duplicate_verdicts_are_attached_and_unrelated_entries_dropped()
    {
        var model = ByRelation(new()
        {
            ["Dup"] = ("duplicate", 0.9),
            ["Upd"] = ("update", 0.8),
            ["Sup"] = ("supersede", 0.7),
            ["Rel"] = ("related", 0.95),
            ["Unr"] = ("unrelated", 0.9),
            ["Meh"] = ("duplicate", 0.3),
        });
        var judge = new JevDuplicateJudge(model, Settings.Decisions(duplicates: DecisionMode.Jev), new AllowOnly("mem"));

        var result = await judge.JudgeAsync(Duplicates(Similar("mem/dup", "Dup"), Similar("mem/upd", "Upd"), Similar("mem/sup", "Sup"), Similar("mem/rel", "Rel"), Similar("mem/unr", "Unr")), CancellationToken.None);

        Assert.Equal(
            [DuplicateVerdict.Duplicate, DuplicateVerdict.Update, DuplicateVerdict.Supersede, DuplicateVerdict.Related],
            result.Similar.Select(s => s.Verdict!.Value));
        Assert.Equal(0.9, result.Similar[0].Confidence);
        Assert.Empty(result.Notes);

        var low = await judge.JudgeAsync(Duplicates(Similar("mem/meh", "Meh")), CancellationToken.None);
        var kept = Assert.Single(low.Similar);
        Assert.Null(kept.Verdict);
    }

    [Fact]
    public async Task The_duplicate_judge_skips_collections_that_do_not_allow_it_and_notes_failures()
    {
        var model = ByRelation(new() { ["Dup"] = ("duplicate", 0.9) });
        var similar = Duplicates(Similar("mem/dup", "Dup"));

        var closed = await new JevDuplicateJudge(model, Settings.Decisions(), new AllowOnly("other")).JudgeAsync(similar, CancellationToken.None);
        Assert.Equal(similar.Similar, closed.Similar);
        Assert.Equal(0, model.Calls);

        var down = await new JevDuplicateJudge(new FakeModel(_ => FakeModel.Failure()), Settings.Decisions(), new AllowOnly("mem")).JudgeAsync(similar, CancellationToken.None);
        Assert.Equal(similar.Similar, down.Similar);
        Assert.Contains("duplicate judge unavailable", Assert.Single(down.Notes), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0.9, true)]
    [InlineData(0.1, false)]
    public async Task The_capture_gate_follows_the_probability(double p, bool remember)
    {
        var gate = new JevCaptureGate(FakeModel.Noul(("remember", p)) is { } fixedAnswer ? new FakeModel(_ => fixedAnswer) : throw new InvalidOperationException(), new AllowOnly("mem"));

        var decision = await gate.EvaluateAsync(new CaptureCandidate(Mem, "We chose SQLite"), CancellationToken.None);

        Assert.Equal(remember, decision.Remember);
        Assert.Equal(p, decision.Likelihood);
        Assert.Equal("jev", decision.Source);
    }

    [Fact]
    public async Task The_capture_gate_remembers_when_it_may_not_ask_or_cannot()
    {
        var model = new FakeModel(_ => FakeModel.Failure());

        var closed = await new JevCaptureGate(model, new AllowOnly("other")).EvaluateAsync(new CaptureCandidate(Mem, "t"), CancellationToken.None);
        var noDestination = await new JevCaptureGate(model, new AllowOnly("mem")).EvaluateAsync(new CaptureCandidate(null, "t"), CancellationToken.None);
        Assert.True(closed.Remember);
        Assert.True(noDestination.Remember);
        Assert.Equal(0, model.Calls);

        var down = await new JevCaptureGate(model, new AllowOnly("mem")).EvaluateAsync(new CaptureCandidate(Mem, "t"), CancellationToken.None);
        Assert.True(down.Remember);
        Assert.Contains("capture gate unavailable", Assert.Single(down.Notes), StringComparison.Ordinal);
    }
}
