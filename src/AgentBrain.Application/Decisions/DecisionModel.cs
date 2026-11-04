using AgentBrain.Application.Common;

namespace AgentBrain.Application.Decisions;

/// <summary>
/// One typed question put to a decision model about a state. Mirrors the three primitives of a System One
/// model: pick one option, score on a rubric, or give the probability that a statement is true.
/// </summary>
public abstract record DecisionQuestion(string Instructions);

/// <summary>Pick one option. <see cref="Options"/> maps each option name to when it applies.</summary>
public sealed record ChoiceQuestion(string Instructions, IReadOnlyList<KeyValuePair<string, string>> Options) : DecisionQuestion(Instructions);

/// <summary>Score the state on a rubric of ordered <see cref="Levels"/> (lowest first).</summary>
public sealed record ScoreQuestion(string Instructions, IReadOnlyList<string> Levels) : DecisionQuestion(Instructions);

/// <summary>The probability, 0 to 1, that the statement in <see cref="DecisionQuestion.Instructions"/> is true of the state.</summary>
public sealed record NoulQuestion(string Instructions) : DecisionQuestion(Instructions);

/// <summary>A state (free text) and named questions about it. Questions are answered independently of each other.</summary>
public sealed record DecisionRequest(string State, IReadOnlyDictionary<string, DecisionQuestion> Questions);

public abstract record DecisionAnswer;

public sealed record ChoiceAnswer(string Choice, double Confidence, IReadOnlyDictionary<string, double> Probabilities) : DecisionAnswer;

public sealed record ScoreAnswer(double Score, double Confidence) : DecisionAnswer;

public sealed record NoulAnswer(double Value) : DecisionAnswer;

/// <summary>Answers by question name, and the input tokens the call cost (0 when served from a cache).</summary>
public sealed record DecisionResponse(IReadOnlyDictionary<string, DecisionAnswer> Answers, int InputTokens, bool FromCache = false)
{
    public double? Noul(string name) => Answers.TryGetValue(name, out var a) && a is NoulAnswer n ? n.Value : null;

    public ChoiceAnswer? Choice(string name) => Answers.TryGetValue(name, out var a) ? a as ChoiceAnswer : null;
}

/// <summary>
/// The one low-level port every model-backed decision consumes. Caching, budgets, timeouts and the privacy
/// filter wrap this single interface rather than being repeated per decision. A failure is an ordinary result
/// (<see cref="ErrorCode.ProviderUnavailable"/>), never an exception: callers fall back to the heuristic.
/// </summary>
public interface IDecisionModel
{
    ValueTask<Result<DecisionResponse>> AskAsync(DecisionRequest request, CancellationToken cancellationToken);
}
