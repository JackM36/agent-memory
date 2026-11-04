using System.Text.Json.Serialization;

namespace AgentBrain.Infrastructure.Local.Configuration;

// The on-disk JSON shape of config.json / config.local.json / the user file (docs/schema/config.v1.schema.json).
// Kept separate from the Application model so the file format can evolve without leaking into use cases.
#pragma warning disable CA1812 // Instantiated by the System.Text.Json source generator.

internal sealed class ConfigFile
{
    [JsonPropertyName("$schema")]
    public string? SchemaUrl { get; set; }

    public int? Schema { get; set; }

    public bool? InheritUser { get; set; }

    public ProjectFile? Project { get; set; }

    public Dictionary<string, CollectionFile>? Collections { get; set; }

    public Dictionary<string, ConnectionFile>? Connections { get; set; }

    public WritesFile? Writes { get; set; }

    public List<string>? SourcePreference { get; set; }

    public SearchFile? Search { get; set; }

    public GetFile? Get { get; set; }

    public Dictionary<string, List<string>>? Aliases { get; set; }

    public string? RuntimeDir { get; set; }

    public string? Proposals { get; set; }

    public bool? ForwardContext { get; set; }
}

internal sealed class ProjectFile
{
    public string? Id { get; set; }

    public List<string>? Technologies { get; set; }

    public Dictionary<string, string>? Packages { get; set; }
}

internal sealed class CollectionFile
{
    public string? Path { get; set; }

    public List<string>? Capabilities { get; set; }

    public bool? ImmediatePublish { get; set; }

    public bool? Enabled { get; set; }
}

internal sealed class ConnectionFile
{
    public string? Url { get; set; }

    public string? CredentialEnv { get; set; }

    public List<string>? Collections { get; set; }

    public int? TimeoutMs { get; set; }

    public ForwardFile? Forward { get; set; }

    public bool? Enabled { get; set; }
}

internal sealed class ForwardFile
{
    public bool? Project { get; set; }

    public bool? Technologies { get; set; }

    public bool? Files { get; set; }

    public bool? Symbols { get; set; }
}

internal sealed class WritesFile
{
    public string? DefaultCollection { get; set; }
}

internal sealed class SearchFile
{
    public string? Relaxation { get; set; }

    public int? MaxResults { get; set; }

    public int? MaxTokens { get; set; }

    public int? MaxBytes { get; set; }

    public int? CandidatesPerSource { get; set; }

    public List<string>? Kinds { get; set; }

    public bool? Log { get; set; }
}

internal sealed class GetFile
{
    public int? MaxBytes { get; set; }

    public int? BatchTargets { get; set; }

    public int? BatchBytes { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    NumberHandling = JsonNumberHandling.Strict)]
[JsonSerializable(typeof(ConfigFile))]
internal sealed partial class ConfigJsonContext : JsonSerializerContext;

#pragma warning restore CA1812
