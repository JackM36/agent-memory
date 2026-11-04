using AgentBrain.Application.Abstractions.Indexing;
using AgentBrain.Application.Retrieval;
using AgentBrain.Application.Retrieval.QueryAnalysis;
using AgentBrain.Application.Retrieval.Ranking;
using AgentBrain.Domain.Entries;
using AgentBrain.Domain.Identity;
using AgentBrain.Domain.Revisions;

namespace AgentBrain.Application.Tests;

public sealed class RankerTests
{
    private static readonly Revision Rev = Revision.Parse("r1-" + new string('a', 26));

    private static IndexCandidate Candidate(
        string reference,
        ApplicabilityScope scope = ApplicabilityScope.General,
        string[]? projects = null,
        string[]? technologies = null,
        int? lexicalRank = 1,
        (int Concept, bool Descriptive)[]? hits = null,
        EntryKind kind = EntryKind.Knowledge,
        bool blocking = false,
        string[]? files = null,
        string[]? symbols = null)
    {
        var entryRef = EntryRef.Parse(reference);
        var metadata = new EntryMetadata
        {
            Id = entryRef.Entry,
            Kind = kind,
            Title = "T",
            Summary = "S",
            Files = files ?? [],
            Symbols = symbols ?? [],
            Resolution = kind == EntryKind.Uncertainty ? Resolution.Unresolved : null,
            Blocking = blocking ? true : null,
        };
        var applicability = new Applicability(scope, projects ?? [], technologies ?? [], [], []);
        return new IndexCandidate(
            new IndexedEntry(entryRef, metadata, applicability, Rev, 100, "x.md"),
            lexicalRank,
            [.. (hits ?? []).Select(h => new ConceptHit(h.Concept, h.Descriptive))],
            false);
    }

    private static QueryTerms Terms(string query)
    {
        QueryAnalyzer.TryAnalyze(query, [], out var terms, out _);
        return terms;
    }

    private static CandidateSet Set(int concepts, params IndexCandidate[] candidates) =>
        new(candidates, [.. Enumerable.Repeat(3, concepts)], 50);

    private static readonly QueryContext Game = new("space-game", ["unity", "csharp"]);

    [Fact]
    public void Same_project_and_general_entries_with_full_coverage_are_admitted_and_ordered()
    {
        var set = Set(2,
            Candidate("kb/general", hits: [(0, true), (1, true)], lexicalRank: 2),
            Candidate("game/memory", ApplicabilityScope.Project, ["space-game"], hits: [(0, true), (1, true)], lexicalRank: 1));

        var (results, _) = Ranker.Rank(set, Terms("save slots"), Game, Relaxation.Default, explain: false);

        Assert.Equal(["game/memory", "kb/general"], results.Select(r => r.Candidate.Entry.Ref.ToString()));
        Assert.Equal(ApplicabilityClass.SameProject, results[0].Applicability);
    }

    [Fact]
    public void Another_projects_entry_needs_more_evidence_than_a_general_one()
    {
        // Coverage 0.5: above the general threshold at default relaxation, below the other-project one.
        var partial = new (int, bool)[] { (0, true), (1, true) };
        var set = Set(4,
            Candidate("kb/general", hits: partial),
            Candidate("shop/memory", ApplicabilityScope.Project, ["web-shop"], hits: partial));

        var (results, _) = Ranker.Rank(set, Terms("one two three four"), Game, Relaxation.Default, explain: false);

        Assert.Equal(["kb/general"], results.Select(r => r.Candidate.Entry.Ref.ToString()));
    }

    [Fact]
    public void Tag_or_body_only_matches_never_qualify_other_projects_below_broad()
    {
        var bodyOnly = Candidate("shop/memory", ApplicabilityScope.Project, ["web-shop"], hits: [(0, false)]);
        var set = Set(1, bodyOnly);

        Assert.Empty(Ranker.Rank(set, Terms("serialization"), Game, Relaxation.Default, false).Results);
    }

    [Fact]
    public void Technology_mismatch_is_penalised_unless_the_query_names_the_technology()
    {
        // Two of three concepts: enough for a general entry, not enough once the mismatch penalty applies.
        var react = Candidate("web/effects", technologies: ["web", "react"], hits: [(0, true), (1, true)]);

        Assert.Empty(Ranker.Rank(Set(3, react), Terms("effect dependencies stale"), Game, Relaxation.Default, false).Results);
        Assert.Single(Ranker.Rank(Set(3, react), Terms("react dependencies stale"), Game, Relaxation.Default, false).Results);
    }

    [Fact]
    public void Structural_matches_admit_without_lexical_evidence()
    {
        var byFile = Candidate("game/save", ApplicabilityScope.Project, ["space-game"], lexicalRank: null, files: ["Assets/Scripts/Save/**"]);
        var context = Game with { Files = ["Assets/Scripts/Save/SaveSystem.cs"] };

        var (results, _) = Ranker.Rank(Set(1, byFile), Terms("unrelated words"), context, Relaxation.Strict, false);

        Assert.Single(results);
    }

    [Fact]
    public void Blocking_uncertainties_surface_separately_on_file_or_symbol_matches()
    {
        var question = Candidate("game/question", ApplicabilityScope.Project, ["space-game"], lexicalRank: null, kind: EntryKind.Uncertainty, blocking: true, symbols: ["SaveSystem"]);
        var context = Game with { Symbols = ["SaveSystem"] };

        var (results, blocking) = Ranker.Rank(Set(1, question), Terms("rename a field"), context, Relaxation.Strict, false);

        Assert.Empty(results);
        Assert.Equal("game/question", Assert.Single(blocking).Candidate.Entry.Ref.ToString());
    }

    [Fact]
    public void Another_projects_blocking_uncertainty_never_blocks()
    {
        var question = Candidate("shop/question", ApplicabilityScope.Project, ["web-shop"], kind: EntryKind.Uncertainty, blocking: true, hits: [(0, true)]);

        var (results, blocking) = Ranker.Rank(Set(1, question), Terms("rounding"), Game, Relaxation.Broad, false);

        Assert.Empty(blocking);
        Assert.Empty(results);
    }

    [Fact]
    public void Concepts_absent_from_the_corpus_do_not_dominate_coverage()
    {
        var entry = Candidate("kb/schedule", hits: [(2, true)]);
        var set = new CandidateSet([entry], [0, 0, 4], 50);

        Assert.Empty(Ranker.Rank(set, Terms("quantum annealing schedule"), Game, Relaxation.Default, false).Results);
    }

    [Fact]
    public void Explain_mode_reports_the_evidence()
    {
        var (results, _) = Ranker.Rank(Set(1, Candidate("kb/a", hits: [(0, true)])), Terms("cache"), Game, Relaxation.Default, explain: true);
        Assert.Contains("coverage=1.00", results[0].Explanation, StringComparison.Ordinal);
        Assert.Contains("class=General", results[0].Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Ties_break_deterministically_on_identity()
    {
        var a = Candidate("kb/b", hits: [(0, true)], lexicalRank: 1);
        var b = Candidate("kb/a", hits: [(0, true)], lexicalRank: 1);

        var (results, _) = Ranker.Rank(Set(1, a, b), Terms("cache"), Game, Relaxation.Default, false);

        Assert.Equal(["kb/a", "kb/b"], results.Select(r => r.Candidate.Entry.Ref.ToString()));
    }
}
