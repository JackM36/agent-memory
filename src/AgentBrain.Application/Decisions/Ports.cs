using AgentBrain.Application.Abstractions.Indexing;
using AgentBrain.Application.Capture;
using AgentBrain.Application.Retrieval;
using AgentBrain.Application.Retrieval.Ranking;
using AgentBrain.Domain.Entries;
using AgentBrain.Domain.Identity;

namespace AgentBrain.Application.Decisions;

// The decision ports (ADR 0010). Each has the pre-existing behaviour as its heuristic implementation here and,
// where a model adds value, a model-backed implementation in AgentBrain.Infrastructure.Jev. Application depends
// only on these interfaces. A model-backed implementation holds the heuristic and falls back to it on any failure.

// ── Relevance: admit, rank, applicability ──────────────────────────────────────────────────────────────────

/// <summary>What a relevance judge sees: the query, the candidates the index returned and the heuristic's inputs.</summary>
public sealed record RelevanceInput(string Query, CandidateSet Set, QueryTerms Terms, QueryContext Context, Relaxation Relaxation, bool Explain);

/// <summary>Admitted candidates in display order, the blocking uncertainties that qualify, and notes such as a fallback.</summary>
public sealed record RelevanceResult(IReadOnlyList<ScoredCandidate> Results, IReadOnlyList<ScoredCandidate> Blocking, IReadOnlyList<string> Notes)
{
    public static RelevanceResult From(IReadOnlyList<ScoredCandidate> results, IReadOnlyList<ScoredCandidate> blocking) => new(results, blocking, []);
}

/// <summary>Decides which candidates answer the query and in what order. Filtering, budgeting and authorization are not its business.</summary>
public interface IRelevanceJudge
{
    ValueTask<RelevanceResult> JudgeAsync(RelevanceInput input, CancellationToken cancellationToken);
}

/// <summary>Rarity-weighted concept coverage against a threshold per applicability class (ADR 0006). The default.</summary>
public sealed class HeuristicRelevanceJudge : IRelevanceJudge
{
    public static HeuristicRelevanceJudge Instance { get; } = new();

    public ValueTask<RelevanceResult> JudgeAsync(RelevanceInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var (results, blocking) = Ranker.Rank(input.Set, input.Terms, input.Context, input.Relaxation, input.Explain);
        return ValueTask.FromResult(RelevanceResult.From(results, blocking));
    }
}

// ── Entry classification (advisory) ────────────────────────────────────────────────────────────────────────

/// <summary>An entry about to be written. <see cref="Excerpt"/> is a short beginning of the body, never all of it.</summary>
public sealed record ClassificationInput(CollectionId? Destination, EntryKind ChosenKind, string Title, string Summary, IReadOnlyList<string> Tags, string? Excerpt);

/// <summary>
/// What the classifier thinks the entry is. It informs; the caller's explicit kind and destination always win
/// (ADR 0010). <see cref="Confidence"/> is the model's confidence in <see cref="Kind"/>.
/// </summary>
public sealed record Classification(EntryKind Kind, double Confidence, bool? TechnologySpecific, string Source)
{
    /// <summary>A note for the caller when the suggestion differs from their choice, else null.</summary>
    public string? Disagreement(EntryKind chosen, double minimumConfidence) =>
        Kind != chosen && Confidence >= minimumConfidence
            ? $"classifier ({Source}) suggests '{WireNames.Kinds.Name(Kind)}' instead of '{WireNames.Kinds.Name(chosen)}' (confidence {Confidence:0.00})"
            : null;
}

/// <summary>A suggestion (null when there is none) and notes such as "the model was unavailable, no suggestion".</summary>
public sealed record ClassificationResult(Classification? Suggestion, IReadOnlyList<string> Notes)
{
    public static ClassificationResult None { get; } = new(null, []);
}

public interface IEntryClassifier
{
    /// <summary>A suggestion, or null when this implementation has none (the heuristic never does: the agent chooses).</summary>
    ValueTask<ClassificationResult> ClassifyAsync(ClassificationInput input, CancellationToken cancellationToken);
}

public sealed class HeuristicEntryClassifier : IEntryClassifier
{
    public static HeuristicEntryClassifier Instance { get; } = new();

    public ValueTask<ClassificationResult> ClassifyAsync(ClassificationInput input, CancellationToken cancellationToken) => ValueTask.FromResult(ClassificationResult.None);
}

// ── Duplicate judgement ────────────────────────────────────────────────────────────────────────────────────

/// <summary>How a new entry relates to an existing similar one.</summary>
public enum DuplicateVerdict
{
    Unrelated,
    Related,
    Duplicate,
    Update,
    Supersede,
}

public sealed record DuplicateInput(CollectionId Collection, string Title, string Summary, IReadOnlyList<string> Tags, IReadOnlyList<SimilarEntry> Similar);

/// <summary>The similar entries, with verdicts where reached, and notes such as a fallback.</summary>
public sealed record DuplicateResult(IReadOnlyList<SimilarEntry> Similar, IReadOnlyList<string> Notes);

public interface IDuplicateJudge
{
    /// <summary>Unrelated entries are dropped and verdicts attached where reached. A model failure is a note, never an exception.</summary>
    ValueTask<DuplicateResult> JudgeAsync(DuplicateInput input, CancellationToken cancellationToken);
}

/// <summary>Keeps the lexically similar entries as found (informational, no verdicts). The default.</summary>
public sealed class HeuristicDuplicateJudge : IDuplicateJudge
{
    public static HeuristicDuplicateJudge Instance { get; } = new();

    public ValueTask<DuplicateResult> JudgeAsync(DuplicateInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        return ValueTask.FromResult(new DuplicateResult(input.Similar, []));
    }
}

// ── Capture gate ───────────────────────────────────────────────────────────────────────────────────────────

public sealed record CaptureCandidate(CollectionId? Destination, string Text);

/// <summary>Whether something is worth remembering. <see cref="Likelihood"/> is null when no model judged it.</summary>
public sealed record CaptureDecision(bool Remember, double? Likelihood, string Source, IReadOnlyList<string> Notes);

public interface ICaptureGate
{
    ValueTask<CaptureDecision> EvaluateAsync(CaptureCandidate candidate, CancellationToken cancellationToken);
}

/// <summary>No gate: whatever the caller decided to save is saved.</summary>
public sealed class HeuristicCaptureGate : ICaptureGate
{
    public static HeuristicCaptureGate Instance { get; } = new();

    public ValueTask<CaptureDecision> EvaluateAsync(CaptureCandidate candidate, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new CaptureDecision(true, null, "none", []));
}

// ── The set ────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>The chosen implementation of every port. Built by the composition root from <see cref="DecisionSettings"/>.</summary>
public sealed record DecisionServices(
    IRelevanceJudge Relevance,
    IEntryClassifier Classifier,
    IDuplicateJudge Duplicates,
    ICaptureGate CaptureGate,
    DecisionSettings Settings,
    IReadOnlyList<string> Problems)
{
    /// <summary>Every port on its heuristic. Used when nothing is configured and in tests.</summary>
    public static DecisionServices Heuristic { get; } = new(
        HeuristicRelevanceJudge.Instance, HeuristicEntryClassifier.Instance, HeuristicDuplicateJudge.Instance, HeuristicCaptureGate.Instance,
        DecisionSettings.Off, []);
}
