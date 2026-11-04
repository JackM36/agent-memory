using AgentBrain.Domain.Matching;
using AgentBrain.Domain.Metadata;
using AgentBrain.Domain.Validation;
using AgentBrain.Domain.Versions;

namespace AgentBrain.Domain.Entries;

/// <summary>Result of validating an entry in its collection: effective applicability (null when invalid) and issues.</summary>
public sealed record EntryValidationResult(Applicability? Applicability, IReadOnlyList<ValidationIssue> Issues)
{
    public bool IsValid => Applicability is not null && !Issues.Any(i => i.Severity == IssueSeverity.Error);
}

/// <summary>Cross-field and collection-dependent rules for entry metadata (plan §5.4).</summary>
public static class EntryValidator
{
    public static EntryValidationResult Validate(EntryMetadata metadata, CollectionDescriptor collection)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(collection);
        var issues = new List<ValidationIssue>();

        CheckText(issues, FieldNames.Title, metadata.Title, EntryLimits.TitleMax);
        CheckText(issues, FieldNames.Summary, metadata.Summary, collection.Limits.SummaryMax);
        CheckCount(issues, FieldNames.Tags, metadata.Tags.Count, EntryLimits.TagsMax);
        CheckCount(issues, FieldNames.Files, metadata.Files.Count, EntryLimits.ListItemsMax);
        CheckCount(issues, FieldNames.Symbols, metadata.Symbols.Count, EntryLimits.ListItemsMax);
        CheckCount(issues, FieldNames.Evidence, metadata.Evidence.Count, EntryLimits.EvidenceItemsMax);

        for (var i = 0; i < metadata.Evidence.Count; i++)
        {
            if (metadata.Evidence[i].Length > EntryLimits.EvidenceItemLengthMax)
            {
                issues.Add(ValidationIssue.Error(IssueCodes.TooLong, $"{FieldNames.Evidence}[{i}]",
                    $"Evidence items are limited to {EntryLimits.EvidenceItemLengthMax} characters."));
            }
        }

        for (var i = 0; i < metadata.Files.Count; i++)
        {
            if (!GlobPattern.TryParse(metadata.Files[i], out _))
            {
                issues.Add(ValidationIssue.Error(IssueCodes.InvalidValue, $"{FieldNames.Files}[{i}]",
                    $"'{metadata.Files[i]}' is not a project-relative glob (no '..', drive letters or backslashes)."));
            }
        }

        CheckUncertaintyFields(issues, metadata);
        CheckLinks(issues, metadata, collection);

        var applicability = Applicability.Resolve(collection.Defaults.Applies, metadata.Applies);
        if (applicability is null)
        {
            issues.Add(ValidationIssue.Error(IssueCodes.Required, $"{FieldNames.Applies}.{FieldNames.Scope}",
                "No applicability scope: set applies.scope or a collection default."));
        }
        else
        {
            CheckApplicability(issues, applicability);
        }

        var valid = !issues.Any(i => i.Severity == IssueSeverity.Error);
        return new EntryValidationResult(valid ? applicability : null, issues);
    }

    private static void CheckText(List<ValidationIssue> issues, string field, string value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            issues.Add(ValidationIssue.Error(IssueCodes.Required, field, $"'{field}' must not be empty."));
        }
        else if (value.Length > max)
        {
            issues.Add(ValidationIssue.Error(IssueCodes.TooLong, field, $"'{field}' is {value.Length} characters; the limit is {max}."));
        }
        else if (value.AsSpan().ContainsAny('\r', '\n'))
        {
            issues.Add(ValidationIssue.Error(IssueCodes.InvalidValue, field, $"'{field}' must be a single line."));
        }
    }

    private static void CheckCount(List<ValidationIssue> issues, string field, int count, int max)
    {
        if (count > max)
        {
            issues.Add(ValidationIssue.Error(IssueCodes.TooMany, field, $"'{field}' has {count} items; the limit is {max}."));
        }
    }

    private static void CheckUncertaintyFields(List<ValidationIssue> issues, EntryMetadata metadata)
    {
        if (metadata.Kind != EntryKind.Uncertainty)
        {
            foreach (var (field, present) in new[]
            {
                (FieldNames.Resolution, metadata.Resolution is not null),
                (FieldNames.Blocking, metadata.Blocking is not null),
                (FieldNames.ResolvedBy, metadata.ResolvedBy is not null),
            })
            {
                if (present)
                {
                    issues.Add(ValidationIssue.Error(IssueCodes.NotApplicable, field, $"'{field}' only applies to uncertainties."));
                }
            }

            return;
        }

        if (metadata.Resolution is null)
        {
            issues.Add(ValidationIssue.Error(IssueCodes.Required, FieldNames.Resolution, "Uncertainties need 'resolution: unresolved' or 'resolution: resolved'."));
        }
        else if (metadata.Resolution == Resolution.Resolved && metadata.Blocking == true)
        {
            issues.Add(ValidationIssue.Error(IssueCodes.Inconsistent, FieldNames.Blocking, "A resolved uncertainty cannot be blocking."));
        }
        else if (metadata.Resolution == Resolution.Unresolved && metadata.ResolvedBy is not null)
        {
            issues.Add(ValidationIssue.Error(IssueCodes.Inconsistent, FieldNames.ResolvedBy, "'resolved-by' requires 'resolution: resolved'."));
        }
    }

    private static void CheckLinks(List<ValidationIssue> issues, EntryMetadata metadata, CollectionDescriptor collection)
    {
        foreach (var (field, link) in new[]
        {
            (FieldNames.SupersededBy, metadata.SupersededBy),
            (FieldNames.Origin, metadata.Origin),
            (FieldNames.ResolvedBy, metadata.ResolvedBy),
        })
        {
            if (link is null)
            {
                continue;
            }

            if (link.Anchor is not null)
            {
                issues.Add(ValidationIssue.Error(IssueCodes.InvalidReference, field, $"'{field}' must link a whole entry, not a section."));
            }

            if (link.Collection == collection.Id && link.Entry == metadata.Id)
            {
                issues.Add(ValidationIssue.Error(IssueCodes.InvalidReference, field, $"'{field}' cannot link the entry to itself."));
            }
        }
    }

    private static void CheckApplicability(List<ValidationIssue> issues, Applicability applicability)
    {
        var projectsField = $"{FieldNames.Applies}.{FieldNames.Projects}";
        if (applicability.Scope == ApplicabilityScope.Project && applicability.Projects.Count == 0)
        {
            issues.Add(ValidationIssue.Error(IssueCodes.Required, projectsField, "Project-scoped entries need at least one project."));
        }
        else if (applicability.Scope == ApplicabilityScope.General && applicability.Projects.Count > 0)
        {
            issues.Add(ValidationIssue.Error(IssueCodes.Inconsistent, projectsField, "General entries must not list projects; use scope 'project' instead."));
        }

        for (var i = 0; i < applicability.Packages.Count; i++)
        {
            if (!VersionRange.TryParse(applicability.Packages[i].Versions, out _))
            {
                issues.Add(ValidationIssue.Error(IssueCodes.InvalidValue, $"{FieldNames.Applies}.{FieldNames.Packages}[{i}].{FieldNames.PackageVersions}",
                    $"'{applicability.Packages[i].Versions}' is not a version range (e.g. '>=1.2 <2.0', '=2022.3.10f1', '>=1 || >=3.1')."));
            }
        }
    }
}
