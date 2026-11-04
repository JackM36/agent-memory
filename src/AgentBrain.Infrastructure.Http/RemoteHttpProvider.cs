using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AgentBrain.Application.Abstractions.Providers;
using AgentBrain.Application.Common;
using AgentBrain.Application.Configuration;
using AgentBrain.Application.Retrieval;
using AgentBrain.Contracts.V1;
using AgentBrain.Domain.Access;
using AgentBrain.Domain.Identity;

namespace AgentBrain.Infrastructure.Http;

/// <summary>
/// A remote Agent Brain host as a retrieval source (spec §8). One hop only: the host searches its own
/// collections and never forwards. Only the context the connection allows is sent. The API key comes from
/// the environment variable named in configuration and is never logged or included in errors.
/// </summary>
public sealed class RemoteHttpProvider : IKnowledgeProvider, IDisposable
{
    public const string ProtocolHeader = ProtocolVersions.Header;

    private readonly HttpClient _http;
    private readonly EffectiveConnection _connection;
    private readonly string? _projectId;

    public RemoteHttpProvider(EffectiveConnection connection, string apiKey, string? projectId, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _connection = connection;
        _projectId = projectId;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.BaseAddress = connection.Url;
        _http.Timeout = Timeout.InfiniteTimeSpan; // The coordinator applies the per-source timeout.
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _http.DefaultRequestHeaders.Add(ProtocolHeader, ProtocolVersions.Protocol.ToString(CultureInfo.InvariantCulture));
        Collections = [.. connection.Collections.Select(id => new CollectionInfo(id, id.Value, Capability.Read, ReadOnly: true, connection.Name))];
    }

    public string SourceId => _connection.Name;

    public IReadOnlyList<CollectionInfo> Collections { get; }

    public void Dispose() => _http.Dispose();

    public async ValueTask<Result<ProviderSearchResult>> SearchAsync(SearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var dto = RequestMapper.FromSearchRequest(request, _connection.Forward, Math.Min(100, request.CandidateLimit));
        if (_connection.Collections.Count > 0 && dto.Filters?.Collections is null)
        {
            dto = dto with { Filters = (dto.Filters ?? new FiltersDto()) with { Collections = [.. _connection.Collections.Select(c => c.Value)] } };
        }

        var response = await SendAsync(HttpMethod.Post, "v1/search", dto, ContractsJsonContext.Default.SearchRequestDto, ContractsJsonContext.Default.SearchResponseDto, cancellationToken);
        if (!response.IsSuccess)
        {
            return response.Error;
        }

        var results = new List<RankedEntry>();
        var blocking = new List<RankedEntry>();
        foreach (var (list, target) in new[] { (response.Value.Results, results), (response.Value.Blocking, blocking) })
        {
            for (var i = 0; i < list.Count; i++)
            {
                var ranked = RequestMapper.ToRanked(list[i], i + 1, SourceId, _projectId);
                if (!ranked.IsSuccess)
                {
                    return ranked.Error;
                }

                target.Add(ranked.Value);
            }
        }

        return new ProviderSearchResult(results, blocking, response.Value.Index == "stale");
    }

    public async ValueTask<Result<Page<RankedEntry>>> ListAsync(ListRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var f = request.Filters;
        var dto = new ListRequestDto(
            new FiltersDto(f.Kinds?.Select(k => Domain.Entries.WireNames.Kinds.Name(k)).ToList(), f.Tags, f.TagMatch == TagMatch.Any,
                (f.Collections ?? _connection.Collections).Select(c => c.Value).ToList()),
            request.Include.History, request.Include.Drafts, request.BlockingOnly, request.Cursor, request.PageSize);
        var response = await SendAsync(HttpMethod.Post, "v1/list", dto, ContractsJsonContext.Default.ListRequestDto, ContractsJsonContext.Default.ListResponseDto, cancellationToken);
        if (!response.IsSuccess)
        {
            return response.Error;
        }

        var items = new List<RankedEntry>();
        foreach (var item in response.Value.Items)
        {
            var ranked = RequestMapper.ToRanked(item, items.Count + 1, SourceId, _projectId);
            if (!ranked.IsSuccess)
            {
                return ranked.Error;
            }

            items.Add(ranked.Value);
        }

        return new Page<RankedEntry>(items, response.Value.NextCursor);
    }

    public async ValueTask<Result<EntryContent>> GetAsync(GetRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var target = request.Ref;
        var path = new StringBuilder().Append(CultureInfo.InvariantCulture, $"v1/entries/{target.Collection}/{target.Entry}?maxBytes={request.MaxBytes}&withRefs={(request.WithReferences ? "true" : "false")}");
        if (target.Anchor is not null)
        {
            path.Append("&section=").Append(Uri.EscapeDataString(target.Anchor));
        }

        var response = await GetJsonAsync(path.ToString(), ContractsJsonContext.Default.EntriesResponseDto, cancellationToken);
        if (!response.IsSuccess)
        {
            return response.Error;
        }

        if (response.Value.Entries.Count == 0)
        {
            return response.Value.Failures.Count > 0
                ? new Error(ErrorCodes.Names.TryParse(response.Value.Failures[0].Code, out var code) ? code : ErrorCode.NotFound, response.Value.Failures[0].Message)
                : Error.NotFound($"'{target}' does not exist.");
        }

        return RequestMapper.ToContent(response.Value.Entries[0], SourceId);
    }

    public async ValueTask<Result<IReadOnlyList<OutgoingReference>>> ReferencesAsync(EntryRef entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var response = await GetJsonAsync($"v1/entries/{entry.Collection}/{entry.Entry}/references", ContractsJsonContext.Default.ReferencesResponseDto, cancellationToken);
        return response.IsSuccess
            ? Result<IReadOnlyList<OutgoingReference>>.Success([.. response.Value.References.Select(RequestMapper.ToReference).OfType<OutgoingReference>()])
            : response.Error;
    }

    public async ValueTask<Result<Page<Backlink>>> BacklinksAsync(EntryRef entry, string? cursor, int pageSize, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var path = $"v1/entries/{entry.Collection}/{entry.Entry}/backlinks?pageSize={pageSize.ToString(CultureInfo.InvariantCulture)}"
            + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
        var response = await GetJsonAsync(path, ContractsJsonContext.Default.BacklinksResponseDto, cancellationToken);
        if (!response.IsSuccess)
        {
            return response.Error;
        }

        var links = response.Value.Backlinks
            .Where(b => EntryRef.TryParse(b.Source, out _))
            .Select(b => new Backlink(EntryRef.Parse(b.Source), b.Anchor, b.Text))
            .ToList();
        return new Page<Backlink>(links, response.Value.NextCursor);
    }

    /// <summary>The collections this key can read on the host, with its effective capabilities (connection test).</summary>
    public async ValueTask<Result<IReadOnlyList<CollectionDto>>> DescribeAsync(CancellationToken cancellationToken)
    {
        var response = await GetJsonAsync("v1/collections", ContractsJsonContext.Default.CollectionsResponseDto, cancellationToken);
        return response.IsSuccess ? Result<IReadOnlyList<CollectionDto>>.Success(response.Value.Collections) : response.Error;
    }

    private ValueTask<Result<TResponse>> GetJsonAsync<TResponse>(string path, JsonTypeInfo<TResponse> responseType, CancellationToken cancellationToken) =>
        SendAsync<string, TResponse>(HttpMethod.Get, path, null, null, responseType, cancellationToken);

    private async ValueTask<Result<TResponse>> SendAsync<TRequest, TResponse>(
        HttpMethod method, string path, TRequest? body, JsonTypeInfo<TRequest>? requestType, JsonTypeInfo<TResponse> responseType, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(method, path);
        if (body is not null && requestType is not null)
        {
            message.Content = new StringContent(JsonSerializer.Serialize(body, requestType), Encoding.UTF8, "application/json");
        }

        try
        {
            using var response = await _http.SendAsync(message, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return JsonSerializer.Deserialize(text, responseType) is { } value
                    ? value
                    : new Error(ErrorCode.ProviderUnavailable, $"{SourceId} returned an empty response.");
            }

            return ToError(response.StatusCode, text);
        }
        catch (HttpRequestException ex)
        {
            return new Error(ErrorCode.ProviderUnavailable, $"{SourceId} is unreachable: {ex.Message}");
        }
        catch (JsonException)
        {
            return new Error(ErrorCode.ProviderUnavailable, $"{SourceId} returned a response this client cannot read; check that client and server versions match.");
        }
    }

    private Error ToError(HttpStatusCode status, string body)
    {
        try
        {
            if (JsonSerializer.Deserialize(body, ContractsJsonContext.Default.ErrorDto) is { } dto)
            {
                return RequestMapper.ToError(dto);
            }
        }
        catch (JsonException)
        {
            // Not an Agent Brain error body (proxy page, etc.): fall through to the status code.
        }

        return status switch
        {
            HttpStatusCode.Unauthorized => new Error(ErrorCode.Unauthorized, $"{SourceId} rejected the API key from ${_connection.Credential.EnvironmentVariable}."),
            HttpStatusCode.Forbidden => new Error(ErrorCode.Forbidden, $"The API key for {SourceId} lacks the required scope."),
            HttpStatusCode.NotFound => Error.NotFound($"{SourceId} does not have that entry."),
            _ => new Error(ErrorCode.ProviderUnavailable, $"{SourceId} answered {(int)status} {status}."),
        };
    }
}
