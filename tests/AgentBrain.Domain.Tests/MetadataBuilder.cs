using AgentBrain.Domain.Entries;
using AgentBrain.Domain.Identity;
using AgentBrain.Domain.Metadata;

namespace AgentBrain.Domain.Tests;

/// <summary>Builds metadata trees from compact literals so tests read like frontmatter.</summary>
internal static class Node
{
    public static MetadataMapping Map(params (string Key, object? Value)[] fields) =>
        new([.. fields.Select(f => new KeyValuePair<string, MetadataNode>(f.Key, From(f.Value)))]);

    public static MetadataNode From(object? value) => value switch
    {
        null => new MetadataNull(),
        MetadataNode node => node,
        string s => new MetadataScalar(s),
        int i => new MetadataScalar(i.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        bool b => new MetadataScalar(b ? "true" : "false"),
        IEnumerable<object> items => new MetadataSequence([.. items.Select(From)]),
        _ => throw new ArgumentOutOfRangeException(nameof(value), value.GetType().Name, "Unsupported literal."),
    };

    public static object[] List(params object[] items) => items;

    /// <summary>A valid minimal memory entry; callers add or override fields.</summary>
    public static MetadataMapping Entry(params (string Key, object? Value)[] overrides)
    {
        var fields = new List<(string Key, object? Value)>
        {
            ("schema", 1), ("id", "save-system"), ("kind", "memory"),
            ("title", "Save system"), ("summary", "How save slots are laid out."),
        };
        foreach (var (key, value) in overrides)
        {
            fields.RemoveAll(f => f.Key == key);
            fields.Add((key, value));
        }

        return Map([.. fields]);
    }

    public static CollectionDescriptor Collection(ApplicabilityOverrides? defaults = null, int summaryMax = EntryLimits.SummaryCeiling) =>
        new(
            CollectionId.Parse("space-game.memory"),
            "Space game memory",
            ReadOnly: false,
            new CollectionDefaults(defaults ?? new ApplicabilityOverrides(ApplicabilityScope.Project, ["space-game"])),
            new EntryLimits(summaryMax),
            []);
}
