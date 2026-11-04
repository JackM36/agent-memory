using System.Globalization;
using System.Text;
using AgentBrain.Application.Abstractions.Indexing;
using AgentBrain.Application.Decisions;
using AgentBrain.Application.Retrieval;
using AgentBrain.Application.Retrieval.Ranking;
using AgentBrain.Domain.Entries;

namespace AgentBrain.Infrastructure.Jev;

/// <summary>
/// The constants of the model-backed relevance judge (ADR 0010). Starting values to be calibrated against the
/// evaluation corpus with a live key; nothing here has been measured against the real service.
/// </summary>
public static class JevRelevanceProfile
{
    /// <summary>Jev mode: minimum answer probability at relaxation 0 (strict) and 1 (broad), per applicability class.</summary>
    public const double SameStrict = 0.50;
    public const double SameBroad = 0.30;
    public const double GeneralStrict = 0.60;
    public const double GeneralBroad = 0.40;
    public const double OtherStrict = 0.75;
    public const double OtherBroad = 0.55;

    /// <summary>Weight of the heuristic score added to the probability in Jev mode (a tie-breaker).</summary>
    public const double JevModeHeuristicWeight = 0.1;

    /// <summary>Hybrid mode: an entry the heuristic admitted is dropped when the probability is not above this.</summary>
    public const double HybridVeto = 0.20;

    /// <summary>Hybrid mode: an entry the heuristic rejected is rescued at or above this (other-project entries need <see cref="HybridRescueOther"/>).</summary>
    public const double HybridRescue = 0.80;

    public const double HybridRescueOther = 0.90;

    /// <summary>Hybrid mode: the probability, centred on 0.5, is scaled by this and added to the heuristic score.</summary>
    public const double HybridScale = 2.0;

    /// <summary>An answer to "applies to this project" below this caps the probability.</summary>
    public const double AppliesFloor = 0.20;

    public static double Threshold(ApplicabilityClass applicability, Relaxation relaxation)
    {
        var (strict, broad) = applicability switch
        {
            ApplicabilityClass.SameProject => (SameStrict, SameBroad),
            ApplicabilityClass.General => (GeneralStrict, GeneralBroad),
            _ => (OtherStrict, OtherBroad),
        };

        return strict + ((broad - strict) * relaxation.Value);
    }
}

/// <summary>
/// Asks the decision model whether each candidate answers the query (and applies to the project), then admits and
/// ranks on the answers: alone in <see cref="DecisionMode.Jev"/>, blended with the heuristic in
/// <see cref="DecisionMode.Hybrid"/>. Only title, summary, kind, tags and applicability of an entry are sent, never its
/// body, and only for collections the policy allows. Blocking uncertainties and candidates that cannot be judged keep
/// the heuristic's verdict, so a model failure degrades ranking quality only.
/// </summary>
public sealed class JevRelevanceJudge(IDecisionModel model, DecisionSettings settings, IExternalDecisionPolicy policy) : IRelevanceJudge
{
    private const string Answers = "answers";
    private const string Applies = "applies";

    public async ValueTask<RelevanceResult> JudgeAsync(RelevanceInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var evaluated = Ranker.Evaluate(input.Set, input.Terms, input.Context, input.Relaxation, input.Explain);
        var hybrid = settings.Relevance == DecisionMode.Hybrid;
        var cap = Math.Min(settings.Jev.RelevancePool, settings.Jev.MaxCallsPerOperation);
        var pool = string.IsNullOrWhiteSpace(input.Query) ? [] : evaluated
            .Where(e => !e.IsBlocking && policy.Allows(e.Scored.Candidate.Entry.Ref.Collection))
            .OrderByDescending(e => e.Scored.Score)
            .ThenBy(e => e.Scored.Applicability)
            .ThenBy(e => e.Scored.Candidate.Entry.Ref.Collection.Value, StringComparer.Ordinal)
            .ThenBy(e => e.Scored.Candidate.Entry.Ref.Entry.Value, StringComparer.Ordinal)
            .Take(Math.Max(0, cap))
            .ToList();

        var probabilities = new Dictionary<EvaluatedCandidate, double>(ReferenceEqualityComparer.Instance);
        var notes = new List<string>();
        if (pool.Count > 0)
        {
            var asked = await Task.WhenAll(pool.Select(e => AskAsync(e, input, cancellationToken)));
            string? failure = null;
            for (var i = 0; i < pool.Count; i++)
            {
                if (asked[i].Probability is { } p)
                {
                    probabilities[pool[i]] = p;
                }
                else
                {
                    failure ??= asked[i].Failure;
                }
            }

            if (failure is not null)
            {
                notes.Add($"relevance judge unavailable ({failure}); heuristic used for {pool.Count - probabilities.Count} of {pool.Count} judged candidates");
            }
        }

        var results = new List<ScoredCandidate>();
        var blocking = new List<ScoredCandidate>();
        foreach (var e in evaluated)
        {
            if (e.IsBlocking)
            {
                if (e.Admitted)
                {
                    blocking.Add(e.Scored);
                }

                continue;
            }

            if (!probabilities.TryGetValue(e, out var p))
            {
                if (e.Admitted)
                {
                    results.Add(input.Explain && pool.Contains(e) ? e.Scored with { Explanation = e.Scored.Explanation + " jev=n/a" } : e.Scored);
                }

                continue;
            }

            var heuristic = e.Scored.Score;
            var (admitted, score) = hybrid
                ? ((e.Admitted && p > JevRelevanceProfile.HybridVeto) || p >= (e.Scored.Applicability == ApplicabilityClass.OtherProject ? JevRelevanceProfile.HybridRescueOther : JevRelevanceProfile.HybridRescue),
                    heuristic + (JevRelevanceProfile.HybridScale * (p - 0.5)))
                : (p >= JevRelevanceProfile.Threshold(e.Scored.Applicability, input.Relaxation), p + (JevRelevanceProfile.JevModeHeuristicWeight * heuristic));
            if (admitted)
            {
                var explanation = input.Explain ? e.Scored.Explanation + " jev=" + p.ToString("0.00", CultureInfo.InvariantCulture) : e.Scored.Explanation;
                results.Add(e.Scored with { Score = score, Explanation = explanation });
            }
        }

        return new RelevanceResult(
            Ranker.OrderForDisplay(results),
            [.. Ranker.OrderForDisplay(blocking).Take(RankingProfileV1.MaxBlocking)],
            notes);
    }

    private async Task<(double? Probability, string? Failure)> AskAsync(EvaluatedCandidate candidate, RelevanceInput input, CancellationToken cancellationToken)
    {
        var entry = candidate.Scored.Candidate.Entry;
        var context = input.Context;
        var hasContext = context.ProjectId is not null || context.Technologies is { Count: > 0 };
        var questions = new Dictionary<string, DecisionQuestion>(StringComparer.Ordinal)
        {
            [Answers] = new NoulQuestion("The entry contains guidance or facts that help with the task in the query"),
        };
        if (hasContext)
        {
            questions[Applies] = new NoulQuestion("The entry applies to the project context described");
        }

        var result = await model.AskAsync(new DecisionRequest(State(input, entry), questions), cancellationToken);
        if (!result.IsSuccess)
        {
            return (null, Reason(result.Error.Message));
        }

        if (result.Value.Noul(Answers) is not { } p)
        {
            return (null, "the response had no answer");
        }

        if (hasContext && result.Value.Noul(Applies) is { } applies && applies < JevRelevanceProfile.AppliesFloor)
        {
            p = Math.Min(p, applies);
        }

        return (Math.Clamp(p, 0, 1), null);
    }

    private static string State(RelevanceInput input, IndexedEntry entry)
    {
        var text = new StringBuilder().Append("Task: ").AppendLine(input.Query);
        var c = input.Context;
        if (c.ProjectId is not null)
        {
            text.Append("Project: ").AppendLine(c.ProjectId);
        }

        Line(text, "Project technologies", c.Technologies);
        Line(text, "Files", c.Files);
        Line(text, "Symbols", c.Symbols);
        var m = entry.Metadata;
        text.Append("Entry kind: ").AppendLine(WireNames.Kinds.Name(m.Kind));
        text.Append("Entry title: ").AppendLine(m.Title);
        text.Append("Entry summary: ").AppendLine(m.Summary);
        Line(text, "Entry tags", m.Tags);
        var a = entry.Applicability;
        text.Append("Entry applies to: ").Append(WireNames.Scopes.Name(a.Scope));
        if (a.Projects.Count > 0)
        {
            text.Append(" (projects: ").AppendJoin(", ", a.Projects).Append(')');
        }

        if (a.Technologies.Count > 0)
        {
            text.Append(" (technologies: ").AppendJoin(", ", a.Technologies).Append(')');
        }

        return text.AppendLine().ToString().ReplaceLineEndings("\n");
    }

    private static void Line(StringBuilder text, string label, IReadOnlyList<string>? values)
    {
        if (values is { Count: > 0 })
        {
            text.Append(label).Append(": ").AppendJoin(", ", values).AppendLine();
        }
    }

    internal static string Reason(string message) => message.StartsWith("decision model unavailable: ", StringComparison.Ordinal) ? message["decision model unavailable: ".Length..] : message;
}
