using System.Globalization;
using AgentBrain.Application.Abstractions.Proposals;
using AgentBrain.Application.Capture;
using AgentBrain.Application.Common;
using AgentBrain.Application.Configuration;
using AgentBrain.Application.Operations.Discovery;
using AgentBrain.Application.Operations.Maintenance;
using AgentBrain.Application.Operations.Proposals;
using AgentBrain.Application.Operations.Publishing;
using AgentBrain.Application.Operations.Routing;
using AgentBrain.Application.Retrieval;
using AgentBrain.Application.Retrieval.Ranking;
using AgentBrain.Application.Writing;
using AgentBrain.Domain.Access;
using AgentBrain.Domain.Entries;
using AgentBrain.Domain.Identity;
using AgentBrain.Domain.Revisions;

namespace AgentBrain.Contracts.V1;

/// <summary>
/// Converts between wire DTOs and application models in both directions: the server parses requests into
/// operation inputs; the remote provider turns application requests into DTOs and responses back into
/// models. Every parse validates and returns an error rather than throwing.
/// </summary>
public static class RequestMapper
{
    // ---- Server side: requests → operation inputs ----

    public static Result<SearchInput> ToSearchInput(SearchRequestDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        var filters = ToFilters(dto.Filters);
        if (!filters.IsSuccess)
        {
            return filters.Error;
        }

        Relaxation? relaxation = null;
        if (dto.Relaxation is not null)
        {
            if (!Relaxation.TryParse(dto.Relaxation, out var value))
            {
                return Error.Validation($"'{dto.Relaxation}' is not strict, default, broad or a number in [0, 1].");
            }

            relaxation = value;
        }

        return new SearchInput(dto.Query)
        {
            Files = dto.Context?.Files,
            Symbols = dto.Context?.Symbols,
            Technologies = dto.Context?.Technologies,
            Packages = dto.Context?.Packages,
            HintTags = dto.Context?.HintTags,
            Kinds = filters.Value.Kinds,
            Tags = filters.Value.Tags,
            TagMatch = filters.Value.TagMatch,
            Collections = filters.Value.Collections,
            Relaxation = relaxation,
            Limit = dto.MaxResults,
            MaxBytes = dto.MaxBytes,
            MaxTokens = dto.MaxTokens,
            Include = new InclusionOptions(dto.IncludeHistory, dto.IncludeIncompatible),
            Explain = dto.Explain,
            ProjectId = dto.Context?.Project,
        };
    }

    public static Result<ListInput> ToListInput(ListRequestDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        var filters = ToFilters(dto.Filters);
        return filters.IsSuccess
            ? new ListInput
            {
                Kinds = filters.Value.Kinds,
                Tags = filters.Value.Tags,
                TagMatch = filters.Value.TagMatch,
                Collections = filters.Value.Collections,
                Include = new InclusionOptions(dto.IncludeHistory, Incompatible: true, dto.IncludeDrafts),
                BlockingOnly = dto.BlockingOnly,
                Cursor = dto.Cursor,
                PageSize = dto.PageSize ?? PageDefaults.PageSize,
            }
            : filters.Error;
    }

    public static Result<ChangeRequest> ToChangeRequest(ChangeRequestDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        if (!Enum.TryParse<ChangeKind>(dto.Kind, ignoreCase: true, out var kind) || !CollectionId.TryParse(dto.Collection, out var collection))
        {
            return Error.Validation("Each change needs 'kind' (create, update or delete) and a valid 'collection'.");
        }

        if (kind == ChangeKind.Create)
        {
            if (!WireNames.Kinds.TryParse(dto.EntryKind ?? "knowledge", out var entryKind) || dto.Title is null || dto.Summary is null)
            {
                return Error.Validation("A create needs 'title', 'summary' and a valid 'entryKind'.");
            }

            EntryId? id = null;
            if (dto.Entry is not null && !EntryId.TryParse(dto.Entry, out id))
            {
                return Error.Validation($"'{dto.Entry}' is not a valid entry ID.");
            }

            return new ChangeRequest(kind, collection)
            {
                NewEntry = new NewEntry(entryKind, dto.Title, dto.Summary, dto.Body ?? "")
                {
                    Id = id, Tags = dto.Tags ?? [], Files = dto.Files ?? [], Symbols = dto.Symbols ?? [], Evidence = dto.Evidence ?? [], Blocking = dto.Blocking,
                    Applies = dto.Technologies is { Count: > 0 } technologies ? new ApplicabilityOverrides(Technologies: technologies) : null,
                },
            };
        }

        if (!EntryId.TryParse(dto.Entry, out var entry) || !Revision.TryParse(dto.BaseRevision, out var revision))
        {
            return Error.Validation("Updates and deletes need 'entry' and 'baseRevision'.");
        }

        EntryRef? supersededBy = null;
        EntryRef? resolvedBy = null;
        if ((dto.SupersededBy is not null && !EntryRef.TryParse(dto.SupersededBy, out supersededBy))
            || (dto.ResolvedBy is not null && !EntryRef.TryParse(dto.ResolvedBy, out resolvedBy)))
        {
            return Error.Validation("'supersededBy' and 'resolvedBy' must be collection-id/entry-id.");
        }

        var request = new ChangeRequest(kind, collection) { Entry = entry, BaseRevision = revision };
        return kind == ChangeKind.Delete ? request : request with
        {
            Patch = new EntryPatch
            {
                Title = dto.Title, Summary = dto.Summary, Body = dto.Body, Tags = dto.Tags, Files = dto.Files, Symbols = dto.Symbols,
                Evidence = dto.Evidence, Archive = dto.Archive, SupersededBy = supersededBy, ResolvedBy = resolvedBy,
            },
        };
    }

    private static Result<SearchFilters> ToFilters(FiltersDto? dto)
    {
        if (dto is null)
        {
            return SearchFilters.None;
        }

        List<EntryKind>? kinds = null;
        foreach (var name in dto.Kinds ?? [])
        {
            if (!WireNames.Kinds.TryParse(name, out var kind))
            {
                return Error.Validation($"'{name}' is not one of: {WireNames.Kinds.Joined}.");
            }

            (kinds ??= []).Add(kind);
        }

        List<CollectionId>? collections = null;
        foreach (var name in dto.Collections ?? [])
        {
            if (!CollectionId.TryParse(name, out var id))
            {
                return Error.Validation($"'{name}' is not a valid collection ID.");
            }

            (collections ??= []).Add(id);
        }

        return new SearchFilters(kinds, dto.Tags?.Select(Names.NormalizeTag).OfType<string>().ToList(), dto.TagsAny ? TagMatch.Any : TagMatch.All, collections);
    }

    // ---- Client side: application requests → DTOs, responses → models ----

    /// <summary>Builds the remote request, forwarding only the context the connection allows (spec §8.2).</summary>
    public static SearchRequestDto FromSearchRequest(SearchRequest request, ForwardPolicy forward, int maxResults)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(forward);
        var c = request.Context;
        var context = new ContextDto(
            forward.Project ? c.ProjectId : null,
            forward.Technologies ? c.Technologies : null,
            forward.Technologies ? c.Packages : null,
            forward.Files ? c.Files : null,
            forward.Symbols ? c.Symbols : null,
            c.HintTags);
        var f = request.Filters;
        return new SearchRequestDto(
            request.Query,
            context,
            new FiltersDto(f.Kinds?.Select(k => WireNames.Kinds.Name(k)).ToList(), f.Tags, f.TagMatch == TagMatch.Any, f.Collections?.Select(id => id.Value).ToList()),
            request.Relaxation.Value.ToString("0.###", CultureInfo.InvariantCulture),
            maxResults,
            MaxBytes: 1_000_000,
            IncludeHistory: request.Include.History,
            IncludeIncompatible: request.Include.Incompatible,
            Explain: request.Explain);
    }

    public static Result<EntrySummary> ToSummary(EntrySummaryDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        if (!EntryRef.TryParse(dto.Ref, out var reference) || !WireNames.Kinds.TryParse(dto.Kind, out var kind)
            || !Revision.TryParse(dto.Rev, out var revision) || !WireNames.Scopes.TryParse(dto.Applicability.Scope, out var scope))
        {
            return new Error(ErrorCode.ProviderUnavailable, $"The remote returned a malformed entry '{dto.Ref}'.");
        }

        var source = WireNames.Sources.TryParse(dto.Source, out var parsedSource) ? parsedSource : ProvenanceSource.Observed;
        var a = dto.Applicability;
        var applicability = new Applicability(scope, a.Projects, a.Technologies, [.. a.Packages.Select(p => new PackageConstraint(p.Name, p.Versions))], a.Platforms);
        return new EntrySummary(reference, kind, dto.Title, dto.Summary, dto.Tags, applicability, source, dto.Size.Bytes, revision, dto.Path);
    }

    public static Result<RankedEntry> ToRanked(EntrySummaryDto dto, int rank, string sourceId, string? projectId)
    {
        var summary = ToSummary(dto);
        return summary.IsSuccess
            ? new RankedEntry(summary.Value, rank, Ranker.Classify(summary.Value.Applicability, projectId), sourceId, dto.Incompatible ?? false, dto.Explain)
            : summary.Error;
    }

    public static Result<EntryContent> ToContent(EntryDto dto, string sourceId)
    {
        ArgumentNullException.ThrowIfNull(dto);
        var summary = ToSummary(dto.Entry);
        if (!summary.IsSuccess)
        {
            return summary.Error;
        }

        var l = dto.Lifecycle;
        var publication = WireNames.Publications.TryParse(l.Publication, out var parsedPublication) ? parsedPublication : Publication.Published;
        Resolution? resolution = l.Resolution is not null && WireNames.Resolutions.TryParse(l.Resolution, out var r) ? r : null;
        DateOnly? archived = l.Archived is not null && DateOnly.TryParseExact(l.Archived, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        EntryRef? supersededBy = EntryRef.TryParse(l.SupersededBy, out var s) ? s : null;
        return new EntryContent(
            summary.Value,
            new LifecycleInfo(publication, archived, supersededBy, resolution, l.Blocking),
            dto.Body,
            dto.Truncated?.OmittedBytes ?? 0,
            dto.Truncated?.RemainingSections ?? [],
            dto.References?.Select(ToReference).OfType<OutgoingReference>().ToList(),
            sourceId);
    }

    public static OutgoingReference? ToReference(ReferenceDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        if (!EntryRef.TryParse(dto.Target, out var target))
        {
            return null;
        }

        ReferenceProblem? problem = dto.Problem switch
        {
            "target_missing" => ReferenceProblem.TargetMissing,
            "collection_unconfigured" => ReferenceProblem.CollectionUnconfigured,
            "source_unavailable" => ReferenceProblem.SourceUnavailable,
            "section_missing" => ReferenceProblem.SectionMissing,
            _ => null,
        };
        return new OutgoingReference(target, dto.Text, problem);
    }

    public static Error ToError(ErrorDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        var code = ErrorCodes.Names.TryParse(dto.Code, out var parsed) ? parsed : ErrorCode.ProviderUnavailable;
        return new Error(code, dto.Message, dto.Details);
    }

    // ---- Client side: remote writes ----

    /// <summary>A change as the host's write endpoints expect it. The host prepares it; the client sends only intent.</summary>
    public static ChangeRequestDto FromChangeRequest(ChangeRequest request, string? note = null, IReadOnlyCollection<string>? allowedSecretRules = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var allow = allowedSecretRules is { Count: > 0 } rules ? rules.ToList() : null;
        var kind = request.Kind.ToString().ToLowerInvariant();
        if (request.NewEntry is { } e)
        {
            return new ChangeRequestDto(
                kind, request.Collection.Value, e.Id?.Value, null, WireNames.Kinds.Name(e.Kind), e.Title, e.Summary, e.Body,
                e.Tags, e.Files, e.Symbols, e.Evidence, Blocking: e.Blocking, Note: note,
                Technologies: e.Applies?.Technologies is { Count: > 0 } technologies ? technologies : null, AllowSecretRules: allow);
        }

        var p = request.Patch;
        return new ChangeRequestDto(
            kind, request.Collection.Value, request.Entry?.Value, request.BaseRevision?.Value, null, p?.Title, p?.Summary, p?.Body,
            p?.Tags, p?.Files, p?.Symbols, p?.Evidence, p?.Archive ?? false, p?.SupersededBy?.ToString(), p?.ResolvedBy?.ToString(),
            p?.Blocking ?? false, note, AllowSecretRules: allow);
    }

    public static Result<WriteResult> ToWriteResult(WriteResponseDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        if (!EntryRef.TryParse(dto.Ref, out var target))
        {
            return Error.Validation($"The host returned an invalid reference '{dto.Ref}'.");
        }

        Revision? revision = Revision.TryParse(dto.Rev, out var r) ? r : null;
        var similar = dto.Similar.Where(s => EntryRef.TryParse(s.Ref, out _)).Select(s => new SimilarEntry(EntryRef.Parse(s.Ref), s.Title, s.Summary, Enum.TryParse<AgentBrain.Application.Decisions.DuplicateVerdict>(s.Verdict, true, out var verdict) ? verdict : null, s.Confidence)).ToList();
        return new WriteResult(target, revision, similar, dto.Notes is { Count: > 0 } ? dto.Notes : null);
    }

    public static Result<ProposalView> ToProposalView(ProposalDto dto, string source)
    {
        ArgumentNullException.ThrowIfNull(dto);
        if (!Enum.TryParse<ProposalStatus>(dto.Status, true, out var status) || !Enum.TryParse<ChangeKind>(dto.Kind, true, out var kind) || !EntryRef.TryParse(dto.Target, out var target))
        {
            return Error.Validation($"The host returned a proposal this client cannot read ({dto.Id}).");
        }

        var created = DateTimeOffset.TryParse(dto.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : DateTimeOffset.MinValue;
        return new ProposalView(dto.Id, status, kind, target, dto.BaseRevision, dto.Title, dto.Summary, dto.ProposedBy, created, dto.Note, dto.ReviewedBy, dto.ReviewNote, dto.Body, source);
    }

    public static IReadOnlyList<PublishedChange> ToPublished(PublishResponseDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        return [.. dto.Published
            .Where(p => EntryRef.TryParse(p.Ref, out _) && Enum.TryParse<ChangeKind>(p.Kind, true, out _))
            .Select(p => new PublishedChange(EntryRef.Parse(p.Ref), Enum.Parse<ChangeKind>(p.Kind, true), Revision.TryParse(p.Rev, out var r) ? r : null))];
    }

    /// <summary>A hosted collection as this client sees it: the key's effective capabilities, attributed to the connection.</summary>
    public static CollectionInfo? ToCollectionInfo(CollectionDto dto, string source)
    {
        ArgumentNullException.ThrowIfNull(dto);
        if (!CollectionId.TryParse(dto.Id, out var id))
        {
            return null;
        }

        var capabilities = Capability.None;
        foreach (var name in dto.Capabilities)
        {
            if (Capabilities.Names.TryParse(name, out var capability))
            {
                capabilities |= capability;
            }
        }

        return new CollectionInfo(id, dto.Title, capabilities, dto.ReadOnly, source);
    }
}
