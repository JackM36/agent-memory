namespace AgentBrain.Domain.Entries;

/// <summary>A package (or engine, e.g. <c>unity</c>) an entry applies to, with a version range in the §5.5 syntax.</summary>
public sealed record PackageConstraint(string Name, string Versions);

/// <summary>
/// Applicability as authored: every field is optional and falls back to the collection default
/// field by field. Missing project metadata never means "general" (spec §5.3).
/// </summary>
public sealed record ApplicabilityOverrides(
    ApplicabilityScope? Scope = null,
    IReadOnlyList<string>? Projects = null,
    IReadOnlyList<string>? Technologies = null,
    IReadOnlyList<PackageConstraint>? Packages = null,
    IReadOnlyList<string>? Platforms = null)
{
    public static readonly ApplicabilityOverrides None = new();
}

/// <summary>Effective applicability of an entry after collection defaults are applied.</summary>
public sealed record Applicability(
    ApplicabilityScope Scope,
    IReadOnlyList<string> Projects,
    IReadOnlyList<string> Technologies,
    IReadOnlyList<PackageConstraint> Packages,
    IReadOnlyList<string> Platforms)
{
    /// <summary>
    /// Applies the entry's own values over the collection default, field by field. Returns null when
    /// neither declares a scope, which validation reports as an error.
    /// </summary>
    public static Applicability? Resolve(ApplicabilityOverrides? collectionDefault, ApplicabilityOverrides? entry)
    {
        var scope = entry?.Scope ?? collectionDefault?.Scope;
        if (scope is null)
        {
            return null;
        }

        return new Applicability(
            scope.Value,
            entry?.Projects ?? collectionDefault?.Projects ?? [],
            entry?.Technologies ?? collectionDefault?.Technologies ?? [],
            entry?.Packages ?? collectionDefault?.Packages ?? [],
            entry?.Platforms ?? collectionDefault?.Platforms ?? []);
    }
}
