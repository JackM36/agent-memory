using AgentBrain.Application.Abstractions.Support;
using AgentBrain.Application.Retrieval;
using AgentBrain.Domain.Access;
using AgentBrain.Domain.Entries;
using AgentBrain.Domain.Identity;
using AgentBrain.Domain.Validation;

namespace AgentBrain.Application.Configuration;

public sealed record ProjectSettings(string Id, string RootDirectory, IReadOnlyList<string> Technologies, IReadOnlyDictionary<string, string> Packages);

/// <summary>A registered local collection. <see cref="Capabilities"/> are as registered; storage may cap them further.</summary>
public sealed record RegisteredCollection(CollectionId Id, string Path, Capability Capabilities, bool ImmediatePublish);

/// <summary>What project context may be forwarded to a remote connection (all false by default).</summary>
public sealed record ForwardPolicy(bool Project, bool Technologies, bool Files, bool Symbols)
{
    public static readonly ForwardPolicy Nothing = new(false, false, false, false);
}

public sealed record EffectiveConnection(
    string Name,
    Uri Url,
    CredentialReference Credential,
    IReadOnlyList<CollectionId> Collections,
    TimeSpan Timeout,
    ForwardPolicy Forward);

public sealed record SearchSettings(
    Relaxation Relaxation,
    int MaxResults,
    int MaxTokens,
    int MaxBytes,
    int CandidatesPerSource,
    IReadOnlyList<EntryKind> Kinds,
    bool Log);

public sealed record GetSettings(int MaxBytes, int BatchTargets, int BatchBytes);

/// <summary>The configuration in force for one invocation, after layering and validation.</summary>
public sealed record EffectiveConfiguration(
    ProjectSettings? Project,
    IReadOnlyList<RegisteredCollection> Collections,
    IReadOnlyList<EffectiveConnection> Connections,
    CollectionId? DefaultWriteCollection,
    IReadOnlyList<string> SourcePreference,
    SearchSettings Search,
    GetSettings Get,
    IReadOnlyList<AliasGroup> Aliases,
    string? RuntimeDirectory,
    string? ProposalsDirectory,
    IReadOnlyDictionary<string, ConfigOrigin> Origins);

public sealed record ConfigDiagnostic(IssueSeverity Severity, string Path, string Message, ConfigOrigin? Origin)
{
    public override string ToString() => $"{Severity.ToString().ToLowerInvariant()} {Path}: {Message}{(Origin is null ? "" : $" [{Origin}]")}";
}

public sealed record ConfigResolution(EffectiveConfiguration Configuration, IReadOnlyList<ConfigDiagnostic> Diagnostics)
{
    public bool HasErrors => Diagnostics.Any(d => d.Severity == IssueSeverity.Error);
}
