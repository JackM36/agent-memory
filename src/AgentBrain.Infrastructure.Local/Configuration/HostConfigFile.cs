using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AgentBrain.Application.Common;
using AgentBrain.Application.Configuration;
using AgentBrain.Domain.Entries;
using AgentBrain.Domain.Identity;

namespace AgentBrain.Infrastructure.Local.Configuration;

/// <summary>Reads and writes <c>host.json</c> (docs/deployment.md). Unknown fields are errors.</summary>
public static class HostConfigFile
{
    public const string FileName = "host.json";

    public static Result<HostConfiguration> Parse(string json, string filePath)
    {
        HostFile? file;
        try
        {
            file = JsonSerializer.Deserialize(json, HostJsonContext.Default.HostFile);
        }
        catch (JsonException ex)
        {
            return new Error(ErrorCode.ConfigInvalid, $"{filePath}: {ex.Message}");
        }

        if (file is null || file.Schema != 1)
        {
            return new Error(ErrorCode.ConfigInvalid, $"{filePath}: expected \"schema\": 1.");
        }

        HostTransport transport = file.Transport switch
        {
            null or "behind-proxy" => HostTransport.BehindProxy,
            "tls" => HostTransport.Tls,
            "local" => HostTransport.Local,
            _ => (HostTransport)(-1),
        };
        if (!Enum.IsDefined(transport))
        {
            return new Error(ErrorCode.ConfigInvalid, $"{filePath}: transport must be tls, behind-proxy or local.");
        }

        if (transport == HostTransport.Tls && string.IsNullOrWhiteSpace(file.Certificate?.Path))
        {
            return new Error(ErrorCode.ConfigInvalid, $"{filePath}: transport tls needs certificate.path.");
        }

        var collections = new List<HostedCollection>();
        foreach (var (name, c) in file.Collections ?? [])
        {
            if (!CollectionId.TryParse(name, out var id))
            {
                return new Error(ErrorCode.ConfigInvalid, $"{filePath}: '{name}' is not a valid collection ID.");
            }

            var storage = c.Storage switch
            {
                null or "sqlite" => HostStorage.Sqlite,
                "markdown" => HostStorage.Markdown,
                _ => (HostStorage)(-1),
            };
            if (!Enum.IsDefined(storage) || (storage == HostStorage.Markdown && string.IsNullOrWhiteSpace(c.Path)))
            {
                return new Error(ErrorCode.ConfigInvalid, $"{filePath}: collection '{name}' needs storage sqlite, or markdown with a path.");
            }

            var scope = WireNames.Scopes.TryParse(c.Scope ?? "general", out var parsedScope) ? parsedScope : ApplicabilityScope.General;
            var source = WireNames.Sources.TryParse(c.Source ?? "curated", out var parsedSource) ? parsedSource : ProvenanceSource.Curated;
            collections.Add(new HostedCollection(
                id, storage, c.Path, c.Title ?? name,
                new ApplicabilityOverrides(scope, c.Projects, c.Technologies),
                source, c.ReadOnly ?? false, c.ImmediatePublish ?? false));
        }

        return new HostConfiguration(
            transport,
            file.Listen ?? "http://0.0.0.0:8080",
            file.Certificate?.Path,
            file.Certificate?.PasswordEnv,
            file.TrustedProxies ?? ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16"],
            collections,
            file.Limits?.MaxResults ?? HostConfiguration.DefaultMaxResults,
            file.Limits?.MaxBytes ?? HostConfiguration.DefaultMaxBytes,
            file.AllowInsecureHttp ?? false);
    }

    /// <summary>
    /// Adds a hosted collection to host.json text (<c>brain-server collections add</c>). Comments are not kept; the
    /// result is re-parsed, so an addition that would make the file invalid is refused instead of written.
    /// </summary>
    public static Result<string> AddCollection(string json, string filePath, HostedCollection collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                ?? throw new JsonException("not an object");
        }
        catch (JsonException ex)
        {
            return new Error(ErrorCode.ConfigInvalid, $"{filePath}: {ex.Message}");
        }

        var collections = root["collections"] as JsonObject ?? [];
        root["collections"] = collections;
        if (collections.ContainsKey(collection.Id.Value))
        {
            return Error.Validation($"Collection '{collection.Id}' is already hosted.");
        }

        var entry = new JsonObject
        {
            ["title"] = collection.Title,
            ["storage"] = collection.Storage == HostStorage.Markdown ? "markdown" : "sqlite",
        };
        if (collection.Path is not null)
        {
            entry["path"] = collection.Path;
        }

        entry["scope"] = "general";
        if (collection.Defaults.Technologies is { Count: > 0 } technologies)
        {
            entry["technologies"] = new JsonArray([.. technologies.Select(t => (JsonNode)t)]);
        }

        entry["readOnly"] = collection.ReadOnly;
        entry["immediatePublish"] = collection.ImmediatePublish;
        collections[collection.Id.Value] = entry;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            root.WriteTo(writer);
        }

        var text = System.Text.Encoding.UTF8.GetString(stream.ToArray()) + "\n";
        var check = Parse(text, filePath);
        return check.IsSuccess ? text : check.Error;
    }

    /// <summary>The host.json <c>brain-server init</c> writes: one SQLite collection, behind a proxy.</summary>
    public static string Initial(string collectionId, string title) => $$"""
        {
          "schema": 1,
          "transport": "behind-proxy",
          "listen": "http://0.0.0.0:8080",
          "collections": {
            "{{collectionId}}": { "title": "{{title}}", "storage": "sqlite", "scope": "general", "source": "curated", "immediatePublish": false }
          },
          "limits": { "maxResults": 100, "maxBytes": 1000000 }
        }

        """;
}

#pragma warning disable CA1812 // Instantiated by the System.Text.Json source generator.
internal sealed class HostFile
{
    public int Schema { get; set; }

    public string? Transport { get; set; }

    public string? Listen { get; set; }

    public CertificateFile? Certificate { get; set; }

    public List<string>? TrustedProxies { get; set; }

    public Dictionary<string, HostCollectionFile>? Collections { get; set; }

    public HostLimitsFile? Limits { get; set; }

    public bool? AllowInsecureHttp { get; set; }
}

internal sealed class CertificateFile
{
    public string? Path { get; set; }

    public string? PasswordEnv { get; set; }
}

internal sealed class HostCollectionFile
{
    public string? Title { get; set; }

    public string? Storage { get; set; }

    public string? Path { get; set; }

    public string? Scope { get; set; }

    public List<string>? Projects { get; set; }

    public List<string>? Technologies { get; set; }

    public string? Source { get; set; }

    public bool? ReadOnly { get; set; }

    public bool? ImmediatePublish { get; set; }
}

internal sealed class HostLimitsFile
{
    public int? MaxResults { get; set; }

    public int? MaxBytes { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(HostFile))]
internal sealed partial class HostJsonContext : JsonSerializerContext;
#pragma warning restore CA1812
