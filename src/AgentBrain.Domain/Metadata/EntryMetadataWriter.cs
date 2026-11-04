using System.Globalization;
using AgentBrain.Domain.Entries;
using AgentBrain.Domain.References;

namespace AgentBrain.Domain.Metadata;

/// <summary>
/// Converts metadata back to a <see cref="MetadataMapping"/> in canonical field order, omitting empty
/// values. Used for revision hashing and, later, for emitting frontmatter.
/// </summary>
public static class EntryMetadataWriter
{
    public static MetadataMapping ToNode(EntryMetadata metadata, bool includeExtensions = true)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var entries = new List<KeyValuePair<string, MetadataNode>>();

        void Add(string key, MetadataNode? node)
        {
            if (node is not null)
            {
                entries.Add(new(key, node));
            }
        }

        Add(FieldNames.Schema, Scalar(metadata.Schema.ToString(CultureInfo.InvariantCulture)));
        Add(FieldNames.Id, Scalar(metadata.Id.Value));
        Add(FieldNames.Kind, Scalar(WireNames.Kinds.Name(metadata.Kind)));
        Add(FieldNames.Title, Scalar(metadata.Title));
        Add(FieldNames.Summary, Scalar(metadata.Summary));
        Add(FieldNames.Tags, List(metadata.Tags));
        Add(FieldNames.Applies, Applies(metadata.Applies));
        Add(FieldNames.Files, List(metadata.Files));
        Add(FieldNames.Symbols, List(metadata.Symbols));
        Add(FieldNames.Source, metadata.Source is { } source ? Scalar(WireNames.Sources.Name(source)) : null);
        Add(FieldNames.Evidence, List(metadata.Evidence));
        Add(FieldNames.Verified, metadata.Verified is { } verified
            ? new MetadataMapping(
            [
                new(FieldNames.VerifiedOn, Date(verified.On)),
                new(FieldNames.VerifiedBy, Scalar(WireNames.VerificationMethods.Name(verified.By))),
            ])
            : null);
        Add(FieldNames.Publication, metadata.Publication is { } publication ? Scalar(WireNames.Publications.Name(publication)) : null);
        Add(FieldNames.Archived, metadata.Archived is { } archived ? Date(archived) : null);
        Add(FieldNames.SupersededBy, metadata.SupersededBy is { } supersededBy ? Scalar(EntryLink.Format(supersededBy)) : null);
        Add(FieldNames.Origin, metadata.Origin is { } origin ? Scalar(EntryLink.Format(origin)) : null);
        Add(FieldNames.Resolution, metadata.Resolution is { } resolution ? Scalar(WireNames.Resolutions.Name(resolution)) : null);
        Add(FieldNames.Blocking, metadata.Blocking is { } blocking ? Scalar(blocking ? "true" : "false") : null);
        Add(FieldNames.ResolvedBy, metadata.ResolvedBy is { } resolvedBy ? Scalar(EntryLink.Format(resolvedBy)) : null);
        Add(FieldNames.Created, metadata.Created is { } created ? Date(created) : null);
        Add(FieldNames.Updated, metadata.Updated is { } updated ? Date(updated) : null);

        if (includeExtensions)
        {
            entries.AddRange(metadata.Extensions);
        }

        return new MetadataMapping(entries);
    }

    private static MetadataMapping? Applies(ApplicabilityOverrides? applies)
    {
        if (applies is null)
        {
            return null;
        }

        var entries = new List<KeyValuePair<string, MetadataNode>>();
        if (applies.Scope is { } scope)
        {
            entries.Add(new(FieldNames.Scope, Scalar(WireNames.Scopes.Name(scope))));
        }

        AddList(entries, FieldNames.Projects, applies.Projects);
        AddList(entries, FieldNames.Technologies, applies.Technologies);
        if (applies.Packages is { Count: > 0 } packages)
        {
            entries.Add(new(FieldNames.Packages, new MetadataSequence(
                [.. packages.Select(p => new MetadataMapping(
                [
                    new(FieldNames.PackageName, Scalar(p.Name)),
                    new(FieldNames.PackageVersions, Scalar(p.Versions)),
                ]))])));
        }

        AddList(entries, FieldNames.Platforms, applies.Platforms);
        return entries.Count == 0 ? null : new MetadataMapping(entries);
    }

    private static void AddList(List<KeyValuePair<string, MetadataNode>> entries, string key, IReadOnlyList<string>? values)
    {
        if (List(values) is { } list)
        {
            entries.Add(new(key, list));
        }
    }

    private static MetadataSequence? List(IReadOnlyList<string>? values) =>
        values is { Count: > 0 } ? new MetadataSequence([.. values.Select(Scalar)]) : null;

    private static MetadataScalar Scalar(string value) => new(value);

    private static MetadataScalar Date(DateOnly date) => new(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
}
