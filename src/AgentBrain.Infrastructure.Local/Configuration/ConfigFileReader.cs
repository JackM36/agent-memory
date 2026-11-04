using System.Text.Json;
using AgentBrain.Application.Common;
using AgentBrain.Application.Configuration;

namespace AgentBrain.Infrastructure.Local.Configuration;

/// <summary>Parses one JSON-with-comments configuration file into a <see cref="ConfigDocument"/>. Unknown fields are errors.</summary>
public static class ConfigFileReader
{
    public static Result<ConfigDocument> Parse(string json, string filePath)
    {
        ArgumentNullException.ThrowIfNull(json);
        ConfigFile? file;
        try
        {
            file = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.ConfigFile);
        }
        catch (JsonException ex)
        {
            var where = ex.LineNumber is { } line ? $" (line {line + 1})" : "";
            return new Error(ErrorCode.ConfigInvalid, $"{filePath}{where}: {ex.Message}", new Dictionary<string, string> { ["file"] = filePath });
        }

        return file is null
            ? new Error(ErrorCode.ConfigInvalid, $"{filePath}: the file is empty or null.", new Dictionary<string, string> { ["file"] = filePath })
            : ToDocument(file);
    }

    public static async ValueTask<Result<ConfigDocument>> ReadAsync(string filePath, CancellationToken cancellationToken)
    {
        string json;
        try
        {
            json = await File.ReadAllTextAsync(filePath, cancellationToken);
        }
        catch (IOException ex)
        {
            return new Error(ErrorCode.ConfigInvalid, $"{filePath}: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return new Error(ErrorCode.ConfigInvalid, $"{filePath}: {ex.Message}");
        }

        return Parse(json, filePath);
    }

    private static ConfigDocument ToDocument(ConfigFile f) => new()
    {
        Schema = f.Schema,
        InheritUser = f.InheritUser,
        Project = f.Project is { } p ? new ProjectSection(p.Id, p.Technologies, p.Packages) : null,
        Collections = f.Collections?.ToDictionary(
            kv => kv.Key,
            kv => new CollectionRegistration(kv.Value.Path, kv.Value.Capabilities, kv.Value.ImmediatePublish, kv.Value.Enabled),
            StringComparer.Ordinal),
        Connections = f.Connections?.ToDictionary(
            kv => kv.Key,
            kv => new ConnectionDefinition(
                kv.Value.Url,
                kv.Value.CredentialEnv,
                kv.Value.Collections,
                kv.Value.TimeoutMs,
                kv.Value.Forward is { } fw ? new ForwardSettings(fw.Project, fw.Technologies, fw.Files, fw.Symbols) : null,
                kv.Value.Enabled),
            StringComparer.Ordinal),
        Writes = f.Writes is { } w ? new WritesSection(w.Memory, w.Knowledge) : null,
        SourcePreference = f.SourcePreference,
        Search = f.Search is { } s ? new SearchSection(s.Relaxation, s.MaxResults, s.MaxTokens, s.MaxBytes, s.CandidatesPerSource, s.Kinds, s.Log) : null,
        Get = f.Get is { } g ? new GetSection(g.MaxBytes, g.BatchTargets, g.BatchBytes) : null,
        Aliases = f.Aliases?.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value, StringComparer.Ordinal),
        RuntimeDir = f.RuntimeDir,
        Proposals = f.Proposals,
        ForwardContext = f.ForwardContext,
    };
}
