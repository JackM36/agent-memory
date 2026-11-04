using System.Globalization;
using AgentBrain.Domain.Entries;
using AgentBrain.Domain.Identity;
using AgentBrain.Domain.References;
using AgentBrain.Domain.Validation;

namespace AgentBrain.Domain.Metadata;

/// <summary>Outcome of reading metadata: the metadata (null when any error was found) and every issue.</summary>
public sealed record MetadataReadResult(EntryMetadata? Metadata, IReadOnlyList<ValidationIssue> Issues);

/// <summary>
/// Maps a <see cref="MetadataMapping"/> to <see cref="EntryMetadata"/> (schema version 1). Unknown
/// fields are errors so typos surface; <c>x-</c> fields are preserved. Values are normalised here
/// (lowercase names, kebab-case tags, de-duplicated lists); cross-field rules live in <see cref="EntryValidator"/>.
/// </summary>
public static class EntryMetadataReader
{
    public static MetadataReadResult Read(MetadataMapping root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return new Reader(root).Read();
    }

    private sealed class Reader(MetadataMapping root)
    {
        private readonly List<ValidationIssue> _issues = [];
        private readonly Dictionary<string, MetadataNode> _fields = new(StringComparer.Ordinal);

        public MetadataReadResult Read()
        {
            // A newer schema may legitimately use fields this version does not know: check it first
            // and report only the version problem.
            var schemaNode = root.Entries.FirstOrDefault(e => e.Key == FieldNames.Schema).Value;
            if (schemaNode is MetadataScalar { Value: var schemaText }
                && int.TryParse(schemaText, NumberStyles.None, CultureInfo.InvariantCulture, out var declared)
                && declared != EntryMetadata.CurrentSchema)
            {
                Error(IssueCodes.UnsupportedSchema, FieldNames.Schema,
                    $"Schema {declared} is not supported; this version reads schema {EntryMetadata.CurrentSchema}.", schemaNode.Line);
                return new MetadataReadResult(null, _issues);
            }

            var extensions = new List<KeyValuePair<string, MetadataNode>>();
            foreach (var (key, value) in root.Entries)
            {
                if (key.StartsWith(FieldNames.ExtensionPrefix, StringComparison.Ordinal))
                {
                    extensions.Add(new(key, value));
                }
                else if (!FieldNames.TopLevel.Contains(key))
                {
                    Error(IssueCodes.UnknownField, key, $"Unknown field '{key}'. Use an '{FieldNames.ExtensionPrefix}' prefix for custom fields.", value.Line);
                }
                else if (!_fields.TryAdd(key, value))
                {
                    Error(IssueCodes.DuplicateField, key, $"Field '{key}' appears more than once.", value.Line);
                }
            }

            var schema = ReadInt(FieldNames.Schema);
            if (schema is null)
            {
                RequireField(FieldNames.Schema);
            }

            var id = ReadParsed(FieldNames.Id, s => EntryId.TryParse(s, out var v) ? v : null, "an entry ID (lowercase words joined by '-', at most 80 characters)");
            var kind = ReadEnum(FieldNames.Kind, WireNames.Kinds);
            var title = ReadString(FieldNames.Title);
            var summary = ReadString(FieldNames.Summary);
            RequireField(FieldNames.Id);
            RequireField(FieldNames.Kind);
            RequireField(FieldNames.Title);
            RequireField(FieldNames.Summary);

            // Placeholders below are never returned: any missing required field is an error and yields null metadata.
            var metadata = new EntryMetadata
            {
                Schema = schema ?? EntryMetadata.CurrentSchema,
                Id = id ?? EntryId.Parse(Slug.Fallback),
                Kind = kind ?? EntryKind.Note,
                Title = title ?? "",
                Summary = summary ?? "",
                Tags = ReadTags(),
                Applies = ReadApplies(),
                Files = ReadStringList(FieldNames.Files) ?? [],
                Symbols = ReadStringList(FieldNames.Symbols) ?? [],
                Source = ReadEnum(FieldNames.Source, WireNames.Sources),
                Evidence = ReadStringList(FieldNames.Evidence) ?? [],
                Verified = ReadVerified(),
                Publication = ReadEnum(FieldNames.Publication, WireNames.Publications),
                Archived = ReadDate(FieldNames.Archived),
                SupersededBy = ReadLink(FieldNames.SupersededBy),
                Origin = ReadLink(FieldNames.Origin),
                Resolution = ReadEnum(FieldNames.Resolution, WireNames.Resolutions),
                Blocking = ReadBool(FieldNames.Blocking),
                ResolvedBy = ReadLink(FieldNames.ResolvedBy),
                Created = ReadDate(FieldNames.Created),
                Updated = ReadDate(FieldNames.Updated),
                Extensions = extensions,
            };

            var hasErrors = _issues.Exists(i => i.Severity == IssueSeverity.Error);
            return new MetadataReadResult(hasErrors ? null : metadata, _issues);
        }

        private List<string> ReadTags()
        {
            var raw = ReadStringList(FieldNames.Tags);
            if (raw is null)
            {
                return [];
            }

            var tags = new List<string>(raw.Count);
            for (var i = 0; i < raw.Count; i++)
            {
                var tag = Names.NormalizeTag(raw[i]);
                if (tag is null)
                {
                    Error(IssueCodes.InvalidValue, $"{FieldNames.Tags}[{i}]", $"Tag '{raw[i]}' has no letters or digits.");
                }
                else if (!tags.Contains(tag))
                {
                    tags.Add(tag);
                }
            }

            return tags;
        }

        private ApplicabilityOverrides? ReadApplies()
        {
            if (!TryGetMapping(FieldNames.Applies, _fields, out var mapping))
            {
                return null;
            }

            var fields = new Dictionary<string, MetadataNode>(StringComparer.Ordinal);
            foreach (var (key, value) in mapping.Entries)
            {
                var path = $"{FieldNames.Applies}.{key}";
                if (!FieldNames.AppliesFields.Contains(key))
                {
                    Error(IssueCodes.UnknownField, path, $"Unknown field '{key}' in '{FieldNames.Applies}'.", value.Line);
                }
                else if (!fields.TryAdd(key, value))
                {
                    Error(IssueCodes.DuplicateField, path, $"Field '{key}' appears more than once.", value.Line);
                }
            }

            ApplicabilityScope? scope = null;
            if (fields.TryGetValue(FieldNames.Scope, out var scopeNode))
            {
                scope = ParseEnum(scopeNode, $"{FieldNames.Applies}.{FieldNames.Scope}", WireNames.Scopes);
            }

            return new ApplicabilityOverrides(
                scope,
                ReadNameList(fields, FieldNames.Projects, Names.IsProjectId, "a project ID"),
                ReadNameList(fields, FieldNames.Technologies, Names.IsTechnicalName, "a technology name"),
                ReadPackages(fields),
                ReadNameList(fields, FieldNames.Platforms, Names.IsTechnicalName, "a platform name"));
        }

        private List<string>? ReadNameList(Dictionary<string, MetadataNode> fields, string key, Func<string, bool> isValid, string description)
        {
            var path = $"{FieldNames.Applies}.{key}";
            if (!fields.TryGetValue(key, out var node) || node is MetadataNull)
            {
                return null;
            }

            var raw = ToStringList(node, path);
            if (raw is null)
            {
                return null;
            }

            var names = new List<string>(raw.Count);
            for (var i = 0; i < raw.Count; i++)
            {
                var name = raw[i].Trim().ToLowerInvariant();
                if (!isValid(name))
                {
                    Error(IssueCodes.InvalidValue, $"{path}[{i}]", $"'{raw[i]}' is not {description}.", node.Line);
                }
                else if (!names.Contains(name))
                {
                    names.Add(name);
                }
            }

            return names;
        }

        private List<PackageConstraint>? ReadPackages(Dictionary<string, MetadataNode> fields)
        {
            var path = $"{FieldNames.Applies}.{FieldNames.Packages}";
            if (!fields.TryGetValue(FieldNames.Packages, out var node) || node is MetadataNull)
            {
                return null;
            }

            if (node is not MetadataSequence sequence)
            {
                Error(IssueCodes.InvalidType, path, "Expected a list of { name, versions }.", node.Line);
                return null;
            }

            var packages = new List<PackageConstraint>(sequence.Items.Count);
            for (var i = 0; i < sequence.Items.Count; i++)
            {
                var itemPath = $"{path}[{i}]";
                if (sequence.Items[i] is not MetadataMapping item)
                {
                    Error(IssueCodes.InvalidType, itemPath, "Expected { name, versions }.", sequence.Items[i].Line);
                    continue;
                }

                string? name = null;
                string? versions = null;
                foreach (var (key, value) in item.Entries)
                {
                    switch (key)
                    {
                        case FieldNames.PackageName:
                            name = ScalarText(value, $"{itemPath}.{key}")?.Trim().ToLowerInvariant();
                            break;
                        case FieldNames.PackageVersions:
                            versions = ScalarText(value, $"{itemPath}.{key}")?.Trim();
                            break;
                        default:
                            Error(IssueCodes.UnknownField, $"{itemPath}.{key}", $"Unknown field '{key}' in a package constraint.", value.Line);
                            break;
                    }
                }

                if (!Names.IsTechnicalName(name))
                {
                    Error(IssueCodes.InvalidValue, $"{itemPath}.{FieldNames.PackageName}", "A package constraint needs a valid package name.", item.Line);
                    continue;
                }

                if (string.IsNullOrEmpty(versions))
                {
                    Error(IssueCodes.Required, $"{itemPath}.{FieldNames.PackageVersions}", "A package constraint needs a version range.", item.Line);
                    continue;
                }

                packages.Add(new PackageConstraint(name, versions));
            }

            return packages;
        }

        private Verification? ReadVerified()
        {
            if (!TryGetMapping(FieldNames.Verified, _fields, out var mapping))
            {
                return null;
            }

            DateOnly? on = null;
            VerificationMethod? by = null;
            foreach (var (key, value) in mapping.Entries)
            {
                var path = $"{FieldNames.Verified}.{key}";
                switch (key)
                {
                    case FieldNames.VerifiedOn:
                        on = ParseDate(value, path);
                        break;
                    case FieldNames.VerifiedBy:
                        by = ParseEnum(value, path, WireNames.VerificationMethods);
                        break;
                    default:
                        Error(IssueCodes.UnknownField, path, $"Unknown field '{key}' in '{FieldNames.Verified}'.", value.Line);
                        break;
                }
            }

            if (on is null || by is null)
            {
                Error(IssueCodes.Required, FieldNames.Verified, $"'{FieldNames.Verified}' needs both '{FieldNames.VerifiedOn}' and '{FieldNames.VerifiedBy}'.", mapping.Line);
                return null;
            }

            return new Verification(on.Value, by.Value);
        }

        private EntryRef? ReadLink(string key)
        {
            var text = ReadString(key);
            if (text is null)
            {
                return null;
            }

            if (!EntryLink.TryParse(text, out var reference))
            {
                Error(IssueCodes.InvalidReference, key, $"'{text}' is not a valid entry link (expected {EntryLink.Scheme}collection-id/entry-id).");
                return null;
            }

            return reference;
        }

        private T? ReadParsed<T>(string key, Func<string, T?> parse, string description)
            where T : class
        {
            var text = ReadString(key);
            if (text is null)
            {
                return null;
            }

            var value = parse(text);
            if (value is null)
            {
                Error(IssueCodes.InvalidValue, key, $"'{text}' is not {description}.");
            }

            return value;
        }

        private string? ReadString(string key) =>
            _fields.TryGetValue(key, out var node) && node is not MetadataNull ? ScalarText(node, key) : null;

        private List<string>? ReadStringList(string key) =>
            _fields.TryGetValue(key, out var node) && node is not MetadataNull ? ToStringList(node, key) : null;

        private int? ReadInt(string key)
        {
            var text = ReadString(key);
            if (text is null)
            {
                return null;
            }

            if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }

            Error(IssueCodes.InvalidValue, key, $"'{text}' is not a whole number.");
            return null;
        }

        private bool? ReadBool(string key)
        {
            var text = ReadString(key);
            return text switch
            {
                null => null,
                "true" => true,
                "false" => false,
                _ => ErrorNull<bool>(key, $"'{text}' is not true or false."),
            };
        }

        private DateOnly? ReadDate(string key) =>
            _fields.TryGetValue(key, out var node) && node is not MetadataNull ? ParseDate(node, key) : null;

        private T? ReadEnum<T>(string key, WireTable<T> table)
            where T : struct, Enum =>
            _fields.TryGetValue(key, out var node) && node is not MetadataNull ? ParseEnum(node, key, table) : null;

        private T? ParseEnum<T>(MetadataNode node, string path, WireTable<T> table)
            where T : struct, Enum
        {
            var text = ScalarText(node, path);
            if (text is null)
            {
                return null;
            }

            if (table.TryParse(text, out var value))
            {
                return value;
            }

            Error(IssueCodes.InvalidValue, path, $"'{text}' is not one of: {table.Joined}.", node.Line);
            return null;
        }

        private DateOnly? ParseDate(MetadataNode node, string path)
        {
            var text = ScalarText(node, path);
            if (text is null)
            {
                return null;
            }

            if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                return date;
            }

            Error(IssueCodes.InvalidValue, path, $"'{text}' is not a date in yyyy-MM-dd form.", node.Line);
            return null;
        }

        private string? ScalarText(MetadataNode node, string path)
        {
            switch (node)
            {
                case MetadataScalar scalar:
                    return scalar.Value;
                case MetadataNull:
                    return null;
                default:
                    Error(IssueCodes.InvalidType, path, "Expected a single value.", node.Line);
                    return null;
            }
        }

        private List<string>? ToStringList(MetadataNode node, string path)
        {
            if (node is not MetadataSequence sequence)
            {
                Error(IssueCodes.InvalidType, path, "Expected a list.", node.Line);
                return null;
            }

            var values = new List<string>(sequence.Items.Count);
            for (var i = 0; i < sequence.Items.Count; i++)
            {
                var text = ScalarText(sequence.Items[i], $"{path}[{i}]");
                if (!string.IsNullOrWhiteSpace(text))
                {
                    values.Add(text.Trim());
                }
            }

            return values;
        }

        private bool TryGetMapping(string key, Dictionary<string, MetadataNode> fields, out MetadataMapping mapping)
        {
            mapping = null!;
            if (!fields.TryGetValue(key, out var node) || node is MetadataNull)
            {
                return false;
            }

            if (node is MetadataMapping found)
            {
                mapping = found;
                return true;
            }

            Error(IssueCodes.InvalidType, key, "Expected a set of fields.", node.Line);
            return false;
        }

        /// <summary>Reports a missing field. A present but unparseable value was already reported by its parser.</summary>
        private void RequireField(string key)
        {
            if (!_fields.TryGetValue(key, out var node) || node is MetadataNull)
            {
                Error(IssueCodes.Required, key, $"Field '{key}' is required.", root.Line);
            }
        }

        private T? ErrorNull<T>(string path, string message)
            where T : struct
        {
            Error(IssueCodes.InvalidValue, path, message);
            return null;
        }

        private void Error(string code, string path, string message, int? line = null) =>
            _issues.Add(ValidationIssue.Error(code, path, message, line ?? (_fields.TryGetValue(path, out var n) ? n.Line : null)));
    }
}
