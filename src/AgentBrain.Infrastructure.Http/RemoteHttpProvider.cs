using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AgentBrain.Application.Abstractions.Providers;
using AgentBrain.Application.Common;
using AgentBrain.Application.Abstractions.Proposals;
using AgentBrain.Application.Configuration;
using AgentBrain.Application.Operations.Maintenance;
using AgentBrain.Application.Operations.Proposals;
using AgentBrain.Application.Operations.Publishing;
using AgentBrain.Application.Operations.Routing;
using AgentBrain.Application.Retrieval;
using AgentBrain.Contracts.V1;
using AgentBrain.Domain.Access;
using AgentBrain.Domain.Identity;
using AgentBrain.Domain.Revisions;

namespace AgentBrain.Infrastructure.Http;

/// <summary>
/// A remote Agent Brain host as a retrieval source (spec §8). One hop only: the host searches its own
/// collections and never forwards. Only the context the connection allows is sent. The API key comes from
/// the environment variable named in configuration and is never logged or included in errors.
/// </summary>
public sealed class RemoteHttpProvider : IKnowledgeProvider, IRemoteWriteTarget, IDisposable
{
    /// <summary>Writes wait longer than searches: the host validates, scans and may write a file.</summary>
    private static readonly TimeSpan MinimumWriteTimeout = TimeSpan.FromSeconds(30);

    public const string ProtocolHeader = ProtocolVersions.Header;

    private readonly HttpClient _http;
    private readonly EffectiveConnection _connection;
    private readonly string? _projectId;
    private readonly SemaphoreSlim _describeLock = new(1, 1);
    private Dictionary<CollectionId, CollectionInfo>? _described;

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

    public void Dispose()
    {
        _http.Dispose();
        _describeLock.Dispose();
    }

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

    // ---- Writes (IRemoteWriteTarget) ----

    public bool MayServe(CollectionId collection) => _connection.Collections.Count == 0 || _connection.Collections.Contains(collection);

    /// <summary>Asks the host once per process which collections this key reads and what it may do there.</summary>
    public async ValueTask<Result<CollectionInfo>> DescribeAsync(CollectionId collection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(collection);
        await _describeLock.WaitAsync(cancellationToken);
        try
        {
            if (_described is null)
            {
                var response = await TimedAsync(DescribeAsync, cancellationToken);
                if (!response.IsSuccess)
                {
                    return response.Error;
                }

                _described = response.Value
                    .Select(c => RequestMapper.ToCollectionInfo(c, SourceId))
                    .OfType<CollectionInfo>()
                    .ToDictionary(c => c.Id);
            }
        }
        finally
        {
            _describeLock.Release();
        }

        return MayServe(collection) && _described.TryGetValue(collection, out var info)
            ? info
            : Error.NotFound($"{SourceId} does not serve collection '{collection}' to this key.");
    }

    public ValueTask<Result<WriteResult>> CreateAsync(ChangeRequest request, IReadOnlyCollection<string> allowedSecretRules, CancellationToken cancellationToken) =>
        WriteAsync(HttpMethod.Post, "v1/entries", RequestMapper.FromChangeRequest(request, null, allowedSecretRules), cancellationToken);

    public ValueTask<Result<WriteResult>> MaintainAsync(MaintenanceAction action, ChangeRequest request, IReadOnlyCollection<string> allowedSecretRules, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var entry = $"v1/entries/{request.Collection}/{request.Entry}";
        var dto = RequestMapper.FromChangeRequest(request, null, allowedSecretRules);
        return action switch
        {
            MaintenanceAction.Archive => WriteAsync(HttpMethod.Post, entry + "/archive", dto, cancellationToken),
            MaintenanceAction.Supersede => WriteAsync(HttpMethod.Post, entry + "/supersede", dto, cancellationToken),
            MaintenanceAction.Resolve => WriteAsync(HttpMethod.Post, entry + "/resolve", dto, cancellationToken),
            _ => WriteAsync(HttpMethod.Put, entry, dto, cancellationToken),
        };
    }

    public async ValueTask<Result<WriteResult>> DeleteAsync(EntryRef target, Revision baseRevision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(baseRevision);
        var path = $"v1/entries/{target.Collection}/{target.Entry}?baseRevision={Uri.EscapeDataString(baseRevision.Value)}";
        var response = await TimedAsync(ct => SendAsync<string, WriteResponseDto>(HttpMethod.Delete, path, null, null, ContractsJsonContext.Default.WriteResponseDto, ct), cancellationToken);
        return response.IsSuccess ? RequestMapper.ToWriteResult(response.Value) : response.Error;
    }

    public async ValueTask<Result<ProposalView>> ProposeAsync(ChangeRequest request, string? note, IReadOnlyCollection<string> allowedSecretRules, CancellationToken cancellationToken)
    {
        var dto = RequestMapper.FromChangeRequest(request, note, allowedSecretRules);
        return Single(await TimedAsync(
            ct => SendAsync(HttpMethod.Post, "v1/proposals", dto, ContractsJsonContext.Default.ChangeRequestDto, ContractsJsonContext.Default.ProposalsResponseDto, ct),
            cancellationToken));
    }

    public async ValueTask<Result<IReadOnlyList<PublishedChange>>> PublishAsync(IReadOnlyList<ChangeRequest> requests, IReadOnlyCollection<string> allowedSecretRules, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var dto = new PublishRequestDto([.. requests.Select(r => RequestMapper.FromChangeRequest(r, null, allowedSecretRules))]);
        var response = await TimedAsync(
            ct => SendAsync(HttpMethod.Post, "v1/publish", dto, ContractsJsonContext.Default.PublishRequestDto, ContractsJsonContext.Default.PublishResponseDto, ct),
            cancellationToken);
        return response.IsSuccess ? Result<IReadOnlyList<PublishedChange>>.Success(RequestMapper.ToPublished(response.Value)) : response.Error;
    }

    public async ValueTask<Result<IReadOnlyList<ProposalView>>> ListProposalsAsync(ProposalStatus? status, CancellationToken cancellationToken)
    {
        var path = status is { } s ? "v1/proposals?status=" + s.ToString().ToLowerInvariant() : "v1/proposals";
        var response = await TimedAsync(ct => GetJsonAsync(path, ContractsJsonContext.Default.ProposalsResponseDto, ct), cancellationToken);
        if (!response.IsSuccess)
        {
            return response.Error;
        }

        var views = new List<ProposalView>();
        foreach (var dto in response.Value.Proposals)
        {
            var view = RequestMapper.ToProposalView(dto, SourceId);
            if (!view.IsSuccess)
            {
                return view.Error;
            }

            views.Add(view.Value);
        }

        return views;
    }

    public async ValueTask<Result<ProposalView>> ShowProposalAsync(string id, CancellationToken cancellationToken) =>
        Single(await TimedAsync(ct => GetJsonAsync($"v1/proposals/{Uri.EscapeDataString(id)}", ContractsJsonContext.Default.ProposalsResponseDto, ct), cancellationToken));

    public async ValueTask<Result<ProposalView>> DecideAsync(string id, bool accept, string? note, CancellationToken cancellationToken)
    {
        var path = $"v1/proposals/{Uri.EscapeDataString(id)}/{(accept ? "accept" : "reject")}";
        return Single(await TimedAsync(
            ct => SendAsync(HttpMethod.Post, path, new DecisionDto(note), ContractsJsonContext.Default.DecisionDto, ContractsJsonContext.Default.ProposalsResponseDto, ct),
            cancellationToken));
    }

    private async ValueTask<Result<WriteResult>> WriteAsync(HttpMethod method, string path, ChangeRequestDto dto, CancellationToken cancellationToken)
    {
        var response = await TimedAsync(
            ct => SendAsync(method, path, dto, ContractsJsonContext.Default.ChangeRequestDto, ContractsJsonContext.Default.WriteResponseDto, ct),
            cancellationToken);
        return response.IsSuccess ? RequestMapper.ToWriteResult(response.Value) : response.Error;
    }

    private Result<ProposalView> Single(Result<ProposalsResponseDto> response) =>
        !response.IsSuccess ? response.Error
        : response.Value.Proposals.Count == 1 ? RequestMapper.ToProposalView(response.Value.Proposals[0], SourceId)
        : new Error(ErrorCode.ProviderUnavailable, $"{SourceId} returned {response.Value.Proposals.Count} proposals where one was expected.");

    /// <summary>
    /// Runs a non-search call under its own deadline. Searches are timed by the coordinator instead; writes and
    /// reviews have no coordinator, so an expired deadline becomes an error here rather than an exception.
    /// </summary>
    private async ValueTask<Result<T>> TimedAsync<T>(Func<CancellationToken, ValueTask<Result<T>>> call, CancellationToken cancellationToken)
    {
        var limit = _connection.Timeout > MinimumWriteTimeout ? _connection.Timeout : MinimumWriteTimeout;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(limit);
        try
        {
            return await call(linked.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new Error(ErrorCode.ProviderUnavailable, $"{SourceId} did not answer within {limit.TotalSeconds:0} seconds. The change may or may not have been applied; check before retrying.");
        }
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
