using AgentBrain.Domain.Identity;
using AgentBrain.Domain.Validation;

namespace AgentBrain.Domain.Entries;

/// <summary>An equivalence group of terms (spec §7.5): any term matches the others as the same concept.</summary>
public sealed record AliasGroup(IReadOnlyList<string> Terms);

/// <summary>Collection-level defaults applied field by field to entries that leave them out.</summary>
public sealed record CollectionDefaults(
    ApplicabilityOverrides? Applies = null,
    ProvenanceSource Source = ProvenanceSource.Observed,
    Publication Publication = Publication.Published);

/// <summary>Content limits (plan §5.4). A collection may lower the summary limit, never raise it.</summary>
public sealed record EntryLimits(int SummaryMax = EntryLimits.SummaryCeiling)
{
    public const int TitleMax = 120;
    public const int SummaryCeiling = 240;
    public const int SummaryFloor = 40;
    public const int TagsMax = 12;
    public const int EvidenceItemsMax = 20;
    public const int EvidenceItemLengthMax = 300;
    public const int ListItemsMax = 50;

    public static readonly EntryLimits Default = new();
}

/// <summary>
/// Collection metadata stored with the content (<c>collection.json</c>): identity, read-only flag,
/// defaults, limits and collection-scoped aliases.
/// </summary>
public sealed record CollectionDescriptor(
    CollectionId Id,
    string Title,
    bool ReadOnly,
    CollectionDefaults Defaults,
    EntryLimits Limits,
    IReadOnlyList<AliasGroup> Aliases)
{
    public const int CurrentSchema = 1;

    public IReadOnlyList<ValidationIssue> Validate()
    {
        var issues = new List<ValidationIssue>();
        if (Defaults.Applies?.Scope is null)
        {
            issues.Add(ValidationIssue.Error(IssueCodes.Required, "defaults.applies.scope",
                "A collection must declare a default applicability scope so entries never become general by omission."));
        }
        else if (Defaults.Applies.Scope == ApplicabilityScope.Project && (Defaults.Applies.Projects?.Count ?? 0) == 0)
        {
            issues.Add(ValidationIssue.Error(IssueCodes.Required, "defaults.applies.projects", "A project-scoped default needs at least one project."));
        }

        if (Limits.SummaryMax is < EntryLimits.SummaryFloor or > EntryLimits.SummaryCeiling)
        {
            issues.Add(ValidationIssue.Error(IssueCodes.InvalidValue, "limits.summaryMax",
                $"summaryMax must be between {EntryLimits.SummaryFloor} and {EntryLimits.SummaryCeiling}."));
        }

        if (string.IsNullOrWhiteSpace(Title))
        {
            issues.Add(ValidationIssue.Error(IssueCodes.Required, "title", "A collection needs a title."));
        }

        return issues;
    }
}
