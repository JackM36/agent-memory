using AgentBrain.Domain.Entries;

namespace AgentBrain.Application.Configuration;

/// <summary>
/// Built-in defaults (plan §5.9) and accepted ranges. The numbers are starting values; budgets and
/// candidate counts are calibration items settled in Phase 2.
/// </summary>
public static class ConfigDefaults
{
    public const int Schema = 1;

    public const string RuntimeDir = "local";
    public const string ProposalsDir = "proposals";

    public const int MaxResults = 10;
    public const int MaxTokens = 1000;
    public const int MaxBytes = 4000;
    public const int CandidatesPerSource = 20;
    public const int GetMaxBytes = 24 * 1024;
    public const int BatchTargets = 10;
    public const int BatchBytes = 48 * 1024;
    public const int ConnectionTimeoutMs = 3000;

    public static readonly IReadOnlyList<string> SearchKinds =
    [
        WireNames.Kinds.Name(EntryKind.Knowledge), WireNames.Kinds.Name(EntryKind.Memory), WireNames.Kinds.Name(EntryKind.Uncertainty),
    ];

    /// <summary>Capabilities of a collection registration that does not list any.</summary>
    public static readonly IReadOnlyList<string> Capabilities = ["read"];

    public static readonly (int Min, int Max) MaxResultsRange = (1, 100);
    public static readonly (int Min, int Max) MaxTokensRange = (50, 100_000);
    public static readonly (int Min, int Max) MaxBytesRange = (256, 10_000_000);
    public static readonly (int Min, int Max) CandidatesRange = (1, 500);
    public static readonly (int Min, int Max) GetBytesRange = (1024, 10_000_000);
    public static readonly (int Min, int Max) BatchTargetsRange = (1, 100);
    public static readonly (int Min, int Max) TimeoutRange = (100, 120_000);

    public static ConfigDocument Document { get; } = new()
    {
        Schema = Schema,
        RuntimeDir = RuntimeDir,
        Proposals = ProposalsDir,
        ForwardContext = true,
        Search = new SearchSection(
            Relaxation: Retrieval.Relaxation.DefaultName,
            MaxResults: MaxResults,
            MaxTokens: MaxTokens,
            MaxBytes: MaxBytes,
            CandidatesPerSource: CandidatesPerSource,
            Kinds: SearchKinds,
            Log: false),
        Get = new GetSection(GetMaxBytes, BatchTargets, BatchBytes),
    };
}
