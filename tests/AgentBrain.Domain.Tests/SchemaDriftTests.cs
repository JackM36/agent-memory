using System.Text.Json;
using AgentBrain.Domain.Entries;
using AgentBrain.Domain.Metadata;

namespace AgentBrain.Domain.Tests;

/// <summary>The hand-written JSON Schema in docs/schema must list exactly the fields the reader accepts.</summary>
public sealed class SchemaDriftTests
{
    private static JsonElement Schema(string file)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "docs", "schema", file);
            if (File.Exists(path))
            {
                return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
            }
        }

        throw new InvalidOperationException($"{file} not found.");
    }

    [Fact]
    public void Frontmatter_schema_matches_the_reader()
    {
        var schema = Schema("frontmatter.v1.schema.json");
        var properties = schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(FieldNames.TopLevel.Order(StringComparer.Ordinal), properties.Order(StringComparer.Ordinal));

        var applies = schema.GetProperty("properties").GetProperty("applies").GetProperty("properties").EnumerateObject().Select(p => p.Name);
        Assert.Equal(FieldNames.AppliesFields.Order(StringComparer.Ordinal), applies.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Frontmatter_schema_enums_match_wire_names()
    {
        var properties = Schema("frontmatter.v1.schema.json").GetProperty("properties");
        static IEnumerable<string> Enum(JsonElement e) => e.GetProperty("enum").EnumerateArray().Select(v => v.GetString()!);

        Assert.Equal(WireNames.Kinds.Names, Enum(properties.GetProperty("kind")));
        Assert.Equal(WireNames.Sources.Names, Enum(properties.GetProperty("source")));
        Assert.Equal(WireNames.Publications.Names, Enum(properties.GetProperty("publication")));
        Assert.Equal(WireNames.Resolutions.Names, Enum(properties.GetProperty("resolution")));
        Assert.Equal(WireNames.VerificationMethods.Names, Enum(properties.GetProperty("verified").GetProperty("properties").GetProperty("by")));
        Assert.Equal(EntryLimits.TitleMax, properties.GetProperty("title").GetProperty("maxLength").GetInt32());
        Assert.Equal(EntryLimits.SummaryCeiling, properties.GetProperty("summary").GetProperty("maxLength").GetInt32());
        Assert.Equal(EntryLimits.TagsMax, properties.GetProperty("tags").GetProperty("maxItems").GetInt32());
    }
}
