using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace AgentBrain.Domain.Identity;

/// <summary>
/// Grammar for the smaller identifiers used in metadata and configuration. Project IDs share the
/// collection-ID grammar; technology, platform and package names allow the punctuation real
/// ecosystems use (<c>c#</c>, <c>node.js</c>, <c>com.unity.entities</c>).
/// </summary>
public static partial class Names
{
    public const int MaxNameLength = 64;
    public const int MaxTagLength = 40;

    public static bool IsProjectId([NotNullWhen(true)] string? value) => CollectionId.IsValid(value);

    /// <summary>Technology, platform or package name (already lowercased).</summary>
    public static bool IsTechnicalName([NotNullWhen(true)] string? value) =>
        value is { Length: > 0 and <= MaxNameLength } && TechnicalName().IsMatch(value);

    /// <summary>Normalised tag: lowercase kebab-case.</summary>
    public static bool IsTag([NotNullWhen(true)] string? value) =>
        value is { Length: > 0 and <= MaxTagLength } && Tag().IsMatch(value);

    /// <summary>Normalises a tag: lowercase, separators become <c>-</c>. Returns null when nothing usable remains.</summary>
    public static string? NormalizeTag(string? raw) => raw is null ? null : Slug.TryCreate(raw, MaxTagLength);

    [GeneratedRegex(@"^[a-z0-9][a-z0-9.+#_-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex TechnicalName();

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex Tag();
}
