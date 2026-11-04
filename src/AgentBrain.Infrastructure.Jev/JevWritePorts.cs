using AgentBrain.Application.Capture;
using AgentBrain.Application.Decisions;
using AgentBrain.Domain.Entries;

namespace AgentBrain.Infrastructure.Jev;

/// <summary>
/// Suggests what an entry is. Advisory: the caller's explicit kind and destination always win. Runs only when the
/// destination collection allows external decisions; otherwise it makes no call and suggests nothing.
/// </summary>
public sealed class JevEntryClassifier(IDecisionModel model, IExternalDecisionPolicy policy) : IEntryClassifier
{
    public const int MaxExcerptChars = 600;

    private static readonly IReadOnlyList<KeyValuePair<string, string>> Kinds =
    [
        new(WireNames.Kinds.Name(EntryKind.Knowledge), "True anywhere and reusable: a general technique or fact with no project specifics"),
        new(WireNames.Kinds.Name(EntryKind.Memory), "True about this repository or project: a decision, constraint or gotcha specific to it"),
        new(WireNames.Kinds.Name(EntryKind.Uncertainty), "An open question or undecided matter"),
        new(WireNames.Kinds.Name(EntryKind.Report), "An account of an incident or investigation"),
        new(WireNames.Kinds.Name(EntryKind.Note), "An informal observation"),
    ];

    public async ValueTask<ClassificationResult> ClassifyAsync(ClassificationInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Destination is not { } destination || !policy.Allows(destination))
        {
            return ClassificationResult.None;
        }

        var excerpt = input.Excerpt is { Length: > MaxExcerptChars } text ? text[..MaxExcerptChars] : input.Excerpt;
        var state = $"Kind chosen by the author: {WireNames.Kinds.Name(input.ChosenKind)}\nTitle: {input.Title}\nSummary: {input.Summary}\n"
            + (input.Tags.Count > 0 ? $"Tags: {string.Join(", ", input.Tags)}\n" : "")
            + (string.IsNullOrWhiteSpace(excerpt) ? "" : $"Beginning of the text: {excerpt}\n");
        var request = new DecisionRequest(state, new Dictionary<string, DecisionQuestion>(StringComparer.Ordinal)
        {
            ["kind"] = new ChoiceQuestion("What kind of entry is this", Kinds),
            ["none_fit"] = new NoulQuestion("None of the listed kinds describes this entry"),
            ["technology_specific"] = new NoulQuestion("The entry only holds for one particular technology, engine or library"),
        });

        var result = await model.AskAsync(request, cancellationToken);
        if (!result.IsSuccess)
        {
            return new ClassificationResult(null, [$"classifier unavailable ({JevRelevanceJudge.Reason(result.Error.Message)}); no suggestion"]);
        }

        var choice = result.Value.Choice("kind");
        if (choice is null || !WireNames.Kinds.TryParse(choice.Choice, out var kind))
        {
            return new ClassificationResult(null, ["classifier unavailable (the response had no usable kind); no suggestion"]);
        }

        if (result.Value.Noul("none_fit") >= 0.5)
        {
            return ClassificationResult.None;
        }

        var technology = result.Value.Noul("technology_specific") is { } t ? t >= 0.5 : (bool?)null;
        return new ClassificationResult(new Classification(kind, choice.Confidence, technology, "jev"), []);
    }
}

/// <summary>
/// Says how a new entry relates to the similar existing ones: duplicate, update, supersede, related or unrelated.
/// Runs only when the collection allows external decisions. A verdict below the minimum confidence is not attached,
/// and a confident "unrelated" drops the entry from the list.
/// </summary>
public sealed class JevDuplicateJudge(IDecisionModel model, DecisionSettings settings, IExternalDecisionPolicy policy) : IDuplicateJudge
{
    private static readonly IReadOnlyList<KeyValuePair<string, string>> Relations =
    [
        new("unrelated", "Different topics"),
        new("related", "Overlapping but distinct: both are worth keeping"),
        new("duplicate", "Says the same thing as the existing entry"),
        new("update", "Same topic: the existing entry should be amended with this"),
        new("supersede", "The new entry replaces the existing one"),
    ];

    public async ValueTask<DuplicateResult> JudgeAsync(DuplicateInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Similar.Count == 0 || !policy.Allows(input.Collection))
        {
            return new DuplicateResult(input.Similar, []);
        }

        var asked = await Task.WhenAll(input.Similar.Take(HeuristicLimit).Select(s => AskAsync(input, s, cancellationToken)));
        var kept = new List<SimilarEntry>();
        string? failure = null;
        foreach (var (entry, verdict, confidence, error) in asked)
        {
            failure ??= error;
            if (verdict == DuplicateVerdict.Unrelated && confidence >= settings.Jev.MinConfidence)
            {
                continue;
            }

            kept.Add(verdict is { } v && confidence >= settings.Jev.MinConfidence ? entry with { Verdict = v, Confidence = confidence } : entry);
        }

        kept.AddRange(input.Similar.Skip(HeuristicLimit));
        return new DuplicateResult(kept, failure is null ? [] : [$"duplicate judge unavailable ({failure}); heuristic list used for some entries"]);
    }

    private const int HeuristicLimit = SimilarEntryFinder.MaxResults;

    private async Task<(SimilarEntry Entry, DuplicateVerdict? Verdict, double Confidence, string? Error)> AskAsync(DuplicateInput input, SimilarEntry existing, CancellationToken cancellationToken)
    {
        var state = $"New entry\nTitle: {input.Title}\nSummary: {input.Summary}\n"
            + (input.Tags.Count > 0 ? $"Tags: {string.Join(", ", input.Tags)}\n" : "")
            + $"\nExisting entry\nTitle: {existing.Title}\nSummary: {existing.Summary}\n";
        var request = new DecisionRequest(state, new Dictionary<string, DecisionQuestion>(StringComparer.Ordinal)
        {
            ["relation"] = new ChoiceQuestion("How does the new entry relate to the existing entry", Relations),
        });
        var result = await model.AskAsync(request, cancellationToken);
        if (!result.IsSuccess)
        {
            return (existing, null, 0, JevRelevanceJudge.Reason(result.Error.Message));
        }

        var choice = result.Value.Choice("relation");
        DuplicateVerdict? verdict = choice?.Choice switch
        {
            "unrelated" => DuplicateVerdict.Unrelated,
            "related" => DuplicateVerdict.Related,
            "duplicate" => DuplicateVerdict.Duplicate,
            "update" => DuplicateVerdict.Update,
            "supersede" => DuplicateVerdict.Supersede,
            _ => null,
        };
        return (existing, verdict, choice?.Confidence ?? 0, verdict is null ? "the response had no usable relation" : null);
    }
}

/// <summary>
/// Judges whether something is worth remembering across sessions. Runs only when the destination allows external
/// decisions; otherwise, and when the model is unavailable, it says remember (the caller already decided to save).
/// </summary>
public sealed class JevCaptureGate(IDecisionModel model, IExternalDecisionPolicy policy) : ICaptureGate
{
    public const double RememberThreshold = 0.5;

    private const string Remember = "remember";

    public async ValueTask<CaptureDecision> EvaluateAsync(CaptureCandidate candidate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.Destination is not { } destination || !policy.Allows(destination))
        {
            return new CaptureDecision(true, null, "none", []);
        }

        var request = new DecisionRequest(candidate.Text, new Dictionary<string, DecisionQuestion>(StringComparer.Ordinal)
        {
            [Remember] = new NoulQuestion("The text states a lasting fact, decision or lesson worth remembering across future sessions, not a one-off request or transient detail"),
        });
        var result = await model.AskAsync(request, cancellationToken);
        if (!result.IsSuccess || result.Value.Noul(Remember) is not { } p)
        {
            var reason = result.IsSuccess ? "the response had no answer" : JevRelevanceJudge.Reason(result.Error.Message);
            return new CaptureDecision(true, null, "none", [$"capture gate unavailable ({reason}); remembering"]);
        }

        return new CaptureDecision(p >= RememberThreshold, p, "jev", []);
    }
}
