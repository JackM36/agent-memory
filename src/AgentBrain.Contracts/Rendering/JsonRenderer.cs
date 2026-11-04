using System.Text.Json;
using AgentBrain.Application.Abstractions.Support;
using AgentBrain.Application.Common;
using AgentBrain.Application.Retrieval;
using AgentBrain.Contracts.V1;
using AgentBrain.Domain.Identity;

namespace AgentBrain.Contracts.Rendering;

/// <summary>Full v1 DTOs as compact JSON: the structured format for scripts and remote clients.</summary>
public sealed class JsonRenderer : IResponseRenderer
{
    public const string Name = "json";

    public string Format => Name;

    public string RenderSearch(SearchResponse response) =>
        JsonSerializer.Serialize(DtoMapper.ToDto(response), ContractsJsonContext.Default.SearchResponseDto);

    public string RenderList(ListResponse response) =>
        JsonSerializer.Serialize(DtoMapper.ToDto(response), ContractsJsonContext.Default.ListResponseDto);

    public string RenderEntries(IReadOnlyList<EntryContent> entries, IReadOnlyList<GetFailure> failures)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(failures);
        var dto = new EntriesResponseDto(
            ProtocolVersions.Protocol,
            [.. entries.Select(DtoMapper.ToDto)],
            [.. failures.Select(f => new GetFailureDto(f.Target.ToString(), ErrorCodes.Names.Name(f.Error.Code), f.Error.Message))]);
        return JsonSerializer.Serialize(dto, ContractsJsonContext.Default.EntriesResponseDto);
    }

    public string RenderReferences(EntryRef source, IReadOnlyList<OutgoingReference> references)
    {
        ArgumentNullException.ThrowIfNull(source);
        var dto = new ReferencesResponseDto(ProtocolVersions.Protocol, source.ToString(), [.. references.Select(DtoMapper.ToDto)]);
        return JsonSerializer.Serialize(dto, ContractsJsonContext.Default.ReferencesResponseDto);
    }

    public string RenderBacklinks(EntryRef target, Page<Backlink> backlinks)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(backlinks);
        var dto = new BacklinksResponseDto(
            ProtocolVersions.Protocol,
            target.ToString(),
            [.. backlinks.Items.Select(b => new BacklinkDto(b.Source.ToString(), b.TargetAnchor, b.LinkText))],
            backlinks.NextCursor);
        return JsonSerializer.Serialize(dto, ContractsJsonContext.Default.BacklinksResponseDto);
    }

    public string RenderCollections(IReadOnlyList<CollectionInfo> collections) =>
        JsonSerializer.Serialize(new CollectionsResponseDto(ProtocolVersions.Protocol, [.. collections.Select(DtoMapper.ToDto)]), ContractsJsonContext.Default.CollectionsResponseDto);

    public string RenderIndex(IndexReport report) =>
        JsonSerializer.Serialize(DtoMapper.ToDto(report), ContractsJsonContext.Default.IndexResponseDto);

    public string RenderError(Error error) =>
        JsonSerializer.Serialize(DtoMapper.ToDto(error), ContractsJsonContext.Default.ErrorDto);
}
