using System.Globalization;
using System.Text;
using AgentBrain.Application.Abstractions.Support;
using AgentBrain.Application.Common;
using AgentBrain.Application.Retrieval;
using AgentBrain.Application.Retrieval.Budgeting;
using AgentBrain.Application.Retrieval.Ranking;
using AgentBrain.Domain.Access;
using AgentBrain.Domain.Entries;
using AgentBrain.Domain.Validation;

namespace AgentBrain.Contracts.V1;

/// <summary>Maps application results to v1 DTOs.</summary>
public static class DtoMapper
{
    public static ErrorDto ToDto(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new ErrorDto(ProtocolVersions.Protocol, ErrorCodes.Names.Name(error.Code), error.Message, error.Details);
    }

    public static EntrySummaryDto ToDto(RankedEntry ranked)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        var e = ranked.Entry;
        return new EntrySummaryDto(
            e.Ref.ToString(),
            WireNames.Kinds.Name(e.Kind),
            e.Title,
            e.Summary,
            e.Tags,
            DescribeApplicability(e.Applicability, ranked.Applicability),
            WireNames.Sources.Name(e.Source),
            new SizeDto(e.SizeBytes, EstimateTokens(e.SizeBytes)),
            e.Revision.Value,
            ranked.SourceId,
            ToDto(e.Applicability),
            e.Locator,
            ranked.Incompatible ? true : null,
            ranked.Conflict is { } c ? new ConflictDto([.. c.Revisions.Select(r => r.Value)], c.Sources) : null,
            ranked.Explanation);
    }

    public static ApplicabilityDto ToDto(Applicability applicability)
    {
        ArgumentNullException.ThrowIfNull(applicability);
        return new ApplicabilityDto(
            WireNames.Scopes.Name(applicability.Scope),
            applicability.Projects,
            applicability.Technologies,
            [.. applicability.Packages.Select(p => new PackageDto(p.Name, p.Versions))],
            applicability.Platforms);
    }

    public static SearchResponseDto ToDto(SearchResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return new SearchResponseDto(
            ProtocolVersions.Protocol,
            ProtocolVersions.Retrieval,
            response.Outcome.IsPartial ? "partial" : "complete",
            response.Outcome.IndexStale ? "stale" : "fresh",
            [.. response.Outcome.Sources.Select(ToDto)],
            [.. response.Blocking.Select(ToDto)],
            [.. response.Results.Select(ToDto)],
            new OmittedDto(response.OmittedForBudget, response.BlockingOmitted),
            new BudgetDto(response.ByteLimit, response.UsedBytes, EstimateTokens(response.UsedBytes)));
    }

    public static ListResponseDto ToDto(ListResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return new ListResponseDto(
            ProtocolVersions.Protocol,
            response.Sources.Any(s => s.State != SourceState.Ok) ? "partial" : "complete",
            response.IndexStale ? "stale" : "fresh",
            [.. response.Sources.Select(ToDto)],
            [.. response.Items.Select(ToDto)],
            response.NextCursor);
    }

    public static EntryDto ToDto(EntryContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var summary = ToDto(new RankedEntry(content.Entry, 1, ApplicabilityClass.General, content.SourceId));
        var l = content.Lifecycle;
        return new EntryDto(
            summary with { Applies = DescribeApplicability(content.Entry.Applicability, null) },
            new LifecycleDto(
                WireNames.Publications.Name(l.Publication),
                l.Archived?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                l.SupersededBy?.ToString(),
                l.Resolution is { } r ? WireNames.Resolutions.Name(r) : null,
                l.Blocking),
            content.Body,
            content.OmittedBytes > 0 ? new TruncationDto(content.OmittedBytes, content.RemainingSections) : null,
            content.References?.Select(ToDto).ToList());
    }

    public static ReferenceDto ToDto(OutgoingReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return new ReferenceDto(reference.Target.ToString(), reference.LinkText, reference.Problem is { } p ? ProblemName(p) : null);
    }

    public static CollectionDto ToDto(CollectionInfo collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        return new CollectionDto(collection.Id.Value, collection.Title, [.. Capabilities.ToNames(collection.Capabilities)], collection.ReadOnly, collection.SourceId);
    }

    public static IndexResponseDto ToDto(IndexReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var s = report.Status;
        return new IndexResponseDto(
            ProtocolVersions.Protocol,
            s.Entries,
            s.Files,
            s.Tokenizer,
            s.LastSync?.ToString("O", CultureInfo.InvariantCulture),
            report.Sync is { } sync ? new SyncDto(sync.Added, sync.Updated, sync.Removed, sync.Unchanged) : null,
            [.. report.Diagnostics.Select(d => new DiagnosticDto(
                d.Collection.Value, d.Path, d.Issue.Severity == IssueSeverity.Error ? "error" : "warning",
                d.Issue.Code, d.Issue.Field, d.Issue.Message, d.Issue.Line))]);
    }

    public static SourceDto ToDto(SourceStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return new SourceDto(status.SourceId, status.State.ToString().ToLowerInvariant(), status.Detail);
    }

    public static string ProblemName(ReferenceProblem problem) => problem switch
    {
        ReferenceProblem.TargetMissing => "target_missing",
        ReferenceProblem.CollectionUnconfigured => "collection_unconfigured",
        ReferenceProblem.SourceUnavailable => "source_unavailable",
        ReferenceProblem.SectionMissing => "section_missing",
        _ => throw new ArgumentOutOfRangeException(nameof(problem)),
    };

    /// <summary>Token estimates are always labelled with <c>~</c>; they are never exact (spec §7.8).</summary>
    public static string EstimateTokens(int bytes) =>
        "~" + ((int)Math.Ceiling(bytes / ResponseBudgeter.BytesPerToken)).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Compact applicability for JSON: <c>project:space-game</c>, <c>other-project:web-shop</c>,
    /// <c>general</c>, followed by technologies, e.g. <c>general unity,csharp</c>.
    /// </summary>
    public static string DescribeApplicability(Applicability applicability, ApplicabilityClass? relation)
    {
        ArgumentNullException.ThrowIfNull(applicability);
        var scope = applicability.Scope == ApplicabilityScope.General
            ? "general"
            : "project:" + string.Join(',', applicability.Projects);
        if (relation == ApplicabilityClass.OtherProject)
        {
            scope = "other-" + scope;
        }

        return applicability.Technologies.Count == 0 ? scope : scope + " " + string.Join(',', applicability.Technologies);
    }

    /// <summary>Applicability for text output: "this project", "OTHER PROJECT web-shop", "general unity" plus version ranges.</summary>
    public static string DescribeForText(Applicability applicability, ApplicabilityClass relation)
    {
        ArgumentNullException.ThrowIfNull(applicability);
        var builder = new StringBuilder(relation switch
        {
            ApplicabilityClass.SameProject => "this project",
            ApplicabilityClass.OtherProject => "OTHER PROJECT " + string.Join(',', applicability.Projects),
            _ => applicability.Technologies.Count == 0 ? "general" : "general " + string.Join(',', applicability.Technologies),
        });
        foreach (var package in applicability.Packages)
        {
            builder.Append(CultureInfo.InvariantCulture, $"·{package.Name}{package.Versions.Replace(" ", "", StringComparison.Ordinal)}");
        }

        return builder.ToString();
    }
}
