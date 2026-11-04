namespace AgentBrain.Application.Configuration;

/// <summary>
/// One configuration file (or the per-call overrides) as authored. Every field is optional; layering
/// decides what is effective (plan §7.4). Format-free: file parsing lives in infrastructure.
/// </summary>
public sealed record ConfigDocument
{
    public int? Schema { get; init; }

    /// <summary>Project files only: false ignores the user file entirely.</summary>
    public bool? InheritUser { get; init; }

    public ProjectSection? Project { get; init; }

    public IReadOnlyDictionary<string, CollectionRegistration>? Collections { get; init; }

    public IReadOnlyDictionary<string, ConnectionDefinition>? Connections { get; init; }

    public WritesSection? Writes { get; init; }

    public IReadOnlyList<string>? SourcePreference { get; init; }

    public SearchSection? Search { get; init; }

    public GetSection? Get { get; init; }

    public IReadOnlyDictionary<string, IReadOnlyList<string>>? Aliases { get; init; }

    public string? RuntimeDir { get; init; }

    public string? Proposals { get; init; }

    /// <summary>False forwards no project context to any remote connection, whatever each connection allows.</summary>
    public bool? ForwardContext { get; init; }
}

public sealed record ProjectSection(
    string? Id = null,
    IReadOnlyList<string>? Technologies = null,
    IReadOnlyDictionary<string, string>? Packages = null);

public sealed record CollectionRegistration(
    string? Path = null,
    IReadOnlyList<string>? Capabilities = null,
    bool? ImmediatePublish = null,
    bool? Enabled = null);

public sealed record ConnectionDefinition(
    string? Url = null,
    string? CredentialEnv = null,
    IReadOnlyList<string>? Collections = null,
    int? TimeoutMs = null,
    ForwardSettings? Forward = null,
    bool? Enabled = null);

public sealed record ForwardSettings(bool? Project = null, bool? Technologies = null, bool? Files = null, bool? Symbols = null);

public sealed record WritesSection(string? Memory = null, string? Knowledge = null);

public sealed record SearchSection(
    string? Relaxation = null,
    int? MaxResults = null,
    int? MaxTokens = null,
    int? MaxBytes = null,
    int? CandidatesPerSource = null,
    IReadOnlyList<string>? Kinds = null,
    bool? Log = null);

public sealed record GetSection(int? MaxBytes = null, int? BatchTargets = null, int? BatchBytes = null);
