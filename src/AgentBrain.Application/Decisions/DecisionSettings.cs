using AgentBrain.Application.Abstractions.Support;
using AgentBrain.Domain.Entries;
using AgentBrain.Domain.Identity;

namespace AgentBrain.Application.Decisions;

/// <summary>Which implementation answers one kind of judgement (ADR 0010).</summary>
public enum DecisionMode
{
    /// <summary>The built-in deterministic behaviour. The default.</summary>
    Heuristic,

    /// <summary>The model decides.</summary>
    Jev,

    /// <summary>Both run and their answers are blended.</summary>
    Hybrid,
}

public static class DecisionModes
{
    public static readonly WireTable<DecisionMode> Names = new(
        (DecisionMode.Heuristic, "heuristic"), (DecisionMode.Jev, "jev"), (DecisionMode.Hybrid, "hybrid"));
}

/// <summary>Connection and limits for the Jev decision model.</summary>
public sealed record JevSettings(
    CredentialReference Credential,
    string Model,
    TimeSpan Timeout,
    int MaxConcurrency,
    int MaxCallsPerOperation,
    double MinConfidence,
    int RelevancePool)
{
    public static JevSettings Default { get; } = new(
        new CredentialReference(DecisionDefaults.ApiKeyEnv),
        DecisionDefaults.Model,
        TimeSpan.FromMilliseconds(DecisionDefaults.TimeoutMs),
        DecisionDefaults.MaxConcurrency,
        DecisionDefaults.MaxCallsPerOperation,
        DecisionDefaults.MinConfidence,
        DecisionDefaults.RelevancePool);

    /// <summary>An alias moves to newer models; evaluation baselines need a fixed one.</summary>
    public bool IsAlias => Model is "jev-latest" or "jev-preview";
}

/// <summary>
/// Which implementation each decision port uses. Everything defaults to the heuristic, so no model is
/// contacted unless the configuration says so (ADR 0010).
/// </summary>
public sealed record DecisionSettings(
    DecisionMode Relevance,
    DecisionMode Classifier,
    DecisionMode Duplicates,
    bool CaptureGate,
    JevSettings Jev)
{
    public static DecisionSettings Off { get; } = new(DecisionMode.Heuristic, DecisionMode.Heuristic, DecisionMode.Heuristic, false, JevSettings.Default);

    /// <summary>True when at least one port needs the decision model.</summary>
    public bool UsesModel => Relevance != DecisionMode.Heuristic || Classifier != DecisionMode.Heuristic || Duplicates != DecisionMode.Heuristic || CaptureGate;
}

/// <summary>Built-in defaults and accepted ranges for the <c>decisions</c> section.</summary>
public static class DecisionDefaults
{
    public const string ApiKeyEnv = "TYPESAFE_API_KEY";
    public const string Model = "jev-1.14.0";
    public const int TimeoutMs = 1500;
    public const int MaxConcurrency = 12;
    public const int MaxCallsPerOperation = 150;
    public const double MinConfidence = 0.6;
    public const int RelevancePool = 100;

    public static readonly (int Min, int Max) TimeoutRange = (100, 120_000);
    public static readonly (int Min, int Max) ConcurrencyRange = (1, 64);
    public static readonly (int Min, int Max) CallsRange = (1, 2000);
    public static readonly (int Min, int Max) PoolRange = (1, 500);
}

/// <summary>
/// Which collections may have their text sent to the decision model (<c>allowExternalDecisions</c>, default false).
/// Text from any other collection is only ever judged by heuristics.
/// </summary>
public interface IExternalDecisionPolicy
{
    bool Allows(CollectionId collection);
}

public sealed class ExternalDecisionPolicy(IReadOnlySet<CollectionId> allowed) : IExternalDecisionPolicy
{
    public static ExternalDecisionPolicy None { get; } = new(new HashSet<CollectionId>());

    public bool Allows(CollectionId collection) => allowed.Contains(collection);
}
