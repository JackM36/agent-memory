using System.Text.Json;
using System.Text.Json.Serialization;
using AgentBrain.Application.Common;
using AgentBrain.Domain.Entries;
using AgentBrain.Domain.Identity;

namespace AgentBrain.Infrastructure.Markdown.Collections;

/// <summary>Reads <c>collection.json</c> (docs/schema/collection.v1.schema.json) into a <see cref="CollectionDescriptor"/>.</summary>
public static class CollectionFileReader
{
    public const string FileName = "collection.json";

    public static Result<CollectionDescriptor> Parse(string json, string filePath)
    {
        CollectionFile? file;
        try
        {
            file = JsonSerializer.Deserialize(json, CollectionJsonContext.Default.CollectionFile);
        }
        catch (JsonException ex)
        {
            return Invalid(filePath, ex.Message);
        }

        if (file is null)
        {
            return Invalid(filePath, "the file is empty.");
        }

        if (file.Schema != CollectionDescriptor.CurrentSchema)
        {
            return Invalid(filePath, $"schema {file.Schema} is not supported; this version reads schema {CollectionDescriptor.CurrentSchema}.");
        }

        if (!CollectionId.TryParse(file.Id, out var id))
        {
            return Invalid(filePath, $"'{file.Id}' is not a valid collection ID.");
        }

        if (!TryParseDefaults(file.Defaults, out var defaults, out var problem))
        {
            return Invalid(filePath, problem);
        }

        var aliases = (file.Aliases ?? []).Select(kv => new AliasGroup([kv.Key.ToLowerInvariant(), .. kv.Value.Select(v => v.ToLowerInvariant())])).ToList();
        var descriptor = new CollectionDescriptor(id, file.Title ?? "", file.ReadOnly ?? false, defaults, new EntryLimits(file.Limits?.SummaryMax ?? EntryLimits.SummaryCeiling), aliases);
        var issues = descriptor.Validate();
        return issues.Count == 0 ? descriptor : Invalid(filePath, string.Join(" ", issues.Select(i => $"{i.Field}: {i.Message}")));
    }

    private static bool TryParseDefaults(DefaultsFile? file, out CollectionDefaults defaults, out string problem)
    {
        defaults = new CollectionDefaults();
        problem = "";
        if (file?.Applies is not { } applies)
        {
            problem = "defaults.applies is required.";
            return false;
        }

        if (!WireNames.Scopes.TryParse(applies.Scope, out var scope))
        {
            problem = $"defaults.applies.scope must be one of: {WireNames.Scopes.Joined}.";
            return false;
        }

        var source = ProvenanceSource.Observed;
        if (file.Source is not null && !WireNames.Sources.TryParse(file.Source, out source))
        {
            problem = $"defaults.source must be one of: {WireNames.Sources.Joined}.";
            return false;
        }

        var publication = Publication.Published;
        if (file.Publication is not null && !WireNames.Publications.TryParse(file.Publication, out publication))
        {
            problem = $"defaults.publication must be one of: {WireNames.Publications.Joined}.";
            return false;
        }

        defaults = new CollectionDefaults(
            new ApplicabilityOverrides(
                scope,
                Lower(applies.Projects),
                Lower(applies.Technologies),
                applies.Packages?.Select(p => new PackageConstraint(p.Name.ToLowerInvariant(), p.Versions)).ToList(),
                Lower(applies.Platforms)),
            source,
            publication);
        return true;
    }

    private static List<string>? Lower(List<string>? values) => values?.Select(v => v.Trim().ToLowerInvariant()).ToList();

    private static Error Invalid(string filePath, string message) =>
        new(ErrorCode.ValidationFailed, $"{filePath}: {message}", new Dictionary<string, string> { ["file"] = filePath });
}

#pragma warning disable CA1812 // Instantiated by the System.Text.Json source generator.
internal sealed class CollectionFile
{
    [JsonPropertyName("$schema")]
    public string? SchemaUrl { get; set; }

    public int Schema { get; set; }

    public string? Id { get; set; }

    public string? Title { get; set; }

    public bool? ReadOnly { get; set; }

    public DefaultsFile? Defaults { get; set; }

    public LimitsFile? Limits { get; set; }

    public Dictionary<string, List<string>>? Aliases { get; set; }
}

internal sealed class DefaultsFile
{
    public AppliesFile? Applies { get; set; }

    public string? Source { get; set; }

    public string? Publication { get; set; }
}

internal sealed class AppliesFile
{
    public string? Scope { get; set; }

    public List<string>? Projects { get; set; }

    public List<string>? Technologies { get; set; }

    public List<PackageFile>? Packages { get; set; }

    public List<string>? Platforms { get; set; }
}

internal sealed class PackageFile
{
    public string Name { get; set; } = "";

    public string Versions { get; set; } = "";
}

internal sealed class LimitsFile
{
    public int? SummaryMax { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(CollectionFile))]
internal sealed partial class CollectionJsonContext : JsonSerializerContext;
#pragma warning restore CA1812
