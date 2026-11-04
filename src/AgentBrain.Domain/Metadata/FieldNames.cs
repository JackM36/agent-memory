namespace AgentBrain.Domain.Metadata;

/// <summary>Frontmatter field names, schema version 1 (plan §5.4), in canonical emission order.</summary>
public static class FieldNames
{
    public const string Schema = "schema";
    public const string Id = "id";
    public const string Kind = "kind";
    public const string Title = "title";
    public const string Summary = "summary";
    public const string Tags = "tags";
    public const string Applies = "applies";
    public const string Files = "files";
    public const string Symbols = "symbols";
    public const string Source = "source";
    public const string Evidence = "evidence";
    public const string Verified = "verified";
    public const string Publication = "publication";
    public const string Archived = "archived";
    public const string SupersededBy = "superseded-by";
    public const string Origin = "origin";
    public const string Resolution = "resolution";
    public const string Blocking = "blocking";
    public const string ResolvedBy = "resolved-by";
    public const string Created = "created";
    public const string Updated = "updated";

    public const string Scope = "scope";
    public const string Projects = "projects";
    public const string Technologies = "technologies";
    public const string Packages = "packages";
    public const string Platforms = "platforms";
    public const string PackageName = "name";
    public const string PackageVersions = "versions";
    public const string VerifiedOn = "on";
    public const string VerifiedBy = "by";

    /// <summary>Prefix of extension fields that are preserved but ignored.</summary>
    public const string ExtensionPrefix = "x-";

    public static readonly IReadOnlyList<string> TopLevel =
    [
        Schema, Id, Kind, Title, Summary, Tags, Applies, Files, Symbols, Source, Evidence, Verified,
        Publication, Archived, SupersededBy, Origin, Resolution, Blocking, ResolvedBy, Created, Updated,
    ];

    public static readonly IReadOnlyList<string> AppliesFields = [Scope, Projects, Technologies, Packages, Platforms];
}
