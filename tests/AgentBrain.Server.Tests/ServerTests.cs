using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgentBrain.Application.Configuration;
using AgentBrain.Application.Retrieval;
using AgentBrain.Contracts.V1;
using AgentBrain.Domain.Identity;
using AgentBrain.Infrastructure.Http;

namespace AgentBrain.Server.Tests;

public sealed class ServerTests : IAsyncLifetime
{
    private TestHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await TestHost.StartAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> Body(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static string Create(string collection, string title, string summary, string body = "Body.") =>
        JsonSerializer.Serialize(new ChangeRequestDto("create", collection, EntryKind: "knowledge", Title: title, Summary: summary, Body: body), ContractsJsonContext.Default.ChangeRequestDto);

    [Fact]
    public async Task Health_needs_no_key_and_the_api_needs_a_valid_one()
    {
        Assert.Equal(HttpStatusCode.OK, (await _host.Client().GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.Client().GetAsync("/v1/collections")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.Client("brain_nope_aaaaaaaaaaaaaaaaaaaaaaaaaaaa").GetAsync("/v1/collections")).StatusCode);

        var error = await Body(await _host.Client().GetAsync("/v1/collections"));
        Assert.Equal("unauthorized", error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Revoked_keys_stop_working_immediately()
    {
        var key = await _host.KeyAsync("collection:*:read");
        Assert.Equal(HttpStatusCode.OK, (await _host.Client(key).GetAsync("/v1/collections")).StatusCode);

        var keyId = key.Split('_')[1];
        await new Infrastructure.Sqlite.SqliteApiKeyStore(_host.Runtime.Database).RevokeAsync(keyId, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.Client(key).GetAsync("/v1/collections")).StatusCode);
    }

    [Fact]
    public async Task Keys_only_see_collections_they_are_scoped_to()
    {
        var writer = _host.Client(await _host.KeyAsync("collection:*:read", "collection:*:maintain"));
        Assert.Equal(HttpStatusCode.Created, (await writer.PostAsync("/v1/entries", Json(Create("team", "Retry policy", "Retry idempotent calls with backoff.")))).StatusCode);

        var notesOnly = _host.Client(await _host.KeyAsync("collection:notes:read"));
        var collections = await Body(await notesOnly.GetAsync("/v1/collections"));
        Assert.Equal(["notes"], collections.GetProperty("collections").EnumerateArray().Select(c => c.GetProperty("id").GetString()));

        var search = await Body(await notesOnly.PostAsync("/v1/search", Json("""{ "query": "retry policy backoff" }""")));
        Assert.Empty(search.GetProperty("results").EnumerateArray());

        var hidden = await notesOnly.GetAsync("/v1/entries/team/retry-policy");
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
    }

    [Fact]
    public async Task A_read_only_key_cannot_write_through_any_endpoint()
    {
        var writer = _host.Client(await _host.KeyAsync("collection:*:read", "collection:*:maintain", "collection:*:propose"));
        await writer.PostAsync("/v1/entries", Json(Create("team", "Existing", "An entry to try changing.")));
        var entry = await Body(await writer.GetAsync("/v1/entries/team/existing"));
        var revision = entry.GetProperty("entries")[0].GetProperty("entry").GetProperty("rev").GetString();
        await writer.PostAsync("/v1/proposals", Json(Create("team", "Proposed", "A proposal.")));
        var proposalId = (await Body(await writer.GetAsync("/v1/proposals"))).GetProperty("proposals")[0].GetProperty("id").GetString();

        var reader = _host.Client(await _host.KeyAsync("collection:*:read"));
        var update = $$"""{ "kind": "update", "collection": "team", "entry": "existing", "baseRevision": "{{revision}}", "summary": "Changed." }""";
        var attempts = new (string Name, Func<Task<HttpResponseMessage>> Call)[]
        {
            ("create", () => reader.PostAsync("/v1/entries", Json(Create("team", "Sneaky", "Should fail.")))),
            ("update", () => reader.PutAsync("/v1/entries/team/existing", Json(update))),
            ("archive", () => reader.PostAsync("/v1/entries/team/existing/archive", Json(update))),
            ("delete", () => reader.DeleteAsync($"/v1/entries/team/existing?baseRevision={revision}")),
            ("propose", () => reader.PostAsync("/v1/proposals", Json(Create("team", "Sneaky proposal", "Should fail.")))),
            ("accept", () => reader.PostAsync($"/v1/proposals/{proposalId}/accept", Json("{}"))),
            ("reject", () => reader.PostAsync($"/v1/proposals/{proposalId}/reject", Json("{}"))),
            ("publish", () => reader.PostAsync("/v1/publish", Json($$"""{ "changes": [ {{Create("fast", "Sneaky", "Should fail.")}} ] }"""))),
            ("reindex", () => reader.PostAsync("/v1/admin/reindex", Json("{}"))),
        };

        foreach (var (name, call) in attempts)
        {
            var response = await call();
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{name}: expected 403, got {(int)response.StatusCode}");
        }

        var unchanged = await Body(await writer.GetAsync("/v1/entries/team/existing"));
        Assert.Equal(revision, unchanged.GetProperty("entries")[0].GetProperty("entry").GetProperty("rev").GetString());
    }

    [Fact]
    public async Task Read_only_collections_refuse_writes_even_with_maintain()
    {
        var admin = _host.Client(await _host.KeyAsync("admin", "collection:*:read", "collection:*:maintain", "collection:*:publish"));
        var response = await admin.PostAsync("/v1/entries", Json(Create("notes", "Note", "Should fail.")));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("read_only", (await Body(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Stale_revisions_conflict_and_secrets_are_rejected()
    {
        var writer = _host.Client(await _host.KeyAsync("collection:*:read", "collection:*:maintain"));
        await writer.PostAsync("/v1/entries", Json(Create("team", "Conflicted", "Original.")));
        var revision = (await Body(await writer.GetAsync("/v1/entries/team/conflicted"))).GetProperty("entries")[0].GetProperty("entry").GetProperty("rev").GetString();
        var update = $$"""{ "kind": "update", "collection": "team", "entry": "conflicted", "baseRevision": "{{revision}}", "summary": "First." }""";
        Assert.Equal(HttpStatusCode.OK, (await writer.PutAsync("/v1/entries/team/conflicted", Json(update))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await writer.PutAsync("/v1/entries/team/conflicted", Json(update))).StatusCode);

        var secret = await writer.PostAsync("/v1/entries", Json(Create("team", "Keys", "Where keys live.", "AKIAIOSFODNN7EXAMPLE")));
        Assert.Equal((HttpStatusCode)422, secret.StatusCode);
        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", await secret.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publishing_is_target_controlled_and_sqlite_batches_are_all_or_nothing()
    {
        var publisher = _host.Client(await _host.KeyAsync("collection:*:read", "collection:*:publish"));

        var disabled = await publisher.PostAsync("/v1/publish", Json($$"""{ "changes": [ {{Create("team", "A", "A.")}} ] }"""));
        Assert.Equal(HttpStatusCode.Forbidden, disabled.StatusCode);
        Assert.Equal("unsupported_operation", (await Body(disabled)).GetProperty("code").GetString());

        var ok = await publisher.PostAsync("/v1/publish", Json($$"""{ "changes": [ {{Create("fast", "One", "First.")}}, {{Create("fast", "Two", "Second.")}} ] }"""));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var revision = (await Body(await publisher.GetAsync("/v1/entries/fast/one"))).GetProperty("entries")[0].GetProperty("entry").GetProperty("rev").GetString();
        var stale = $$"""{ "kind": "update", "collection": "fast", "entry": "one", "baseRevision": "{{revision}}", "summary": "Changed." }""";
        await publisher.PostAsync("/v1/publish", Json($$"""{ "changes": [ {{stale}} ] }"""));

        // The second batch: a new entry plus the now-stale update. Nothing may be applied.
        var mixed = await publisher.PostAsync("/v1/publish", Json($$"""{ "changes": [ {{Create("fast", "Three", "Third.")}}, {{stale}} ] }"""));
        Assert.Equal(HttpStatusCode.Conflict, mixed.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await publisher.GetAsync("/v1/entries/fast/three")).StatusCode);
    }

    [Fact]
    public async Task Protocol_mismatch_is_rejected()
    {
        var client = _host.Client(await _host.KeyAsync("collection:*:read"));
        client.DefaultRequestHeaders.Add(ProtocolVersions.Header, "99");
        var response = await client.GetAsync("/v1/collections");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("protocol_version_unsupported", (await Body(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Hosted_markdown_is_searchable_by_section()
    {
        var reader = _host.Client(await _host.KeyAsync("collection:*:read"));
        var search = await Body(await reader.PostAsync("/v1/search", Json("""{ "query": "restart ingestion service" }""")));
        Assert.Equal("notes/ops", search.GetProperty("results")[0].GetProperty("ref").GetString());

        var section = await Body(await reader.GetAsync("/v1/entries/notes/ops?section=escalate"));
        Assert.Contains("on-call", section.GetProperty("entries")[0].GetProperty("body").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_remote_provider_searches_gets_and_lists_through_http()
    {
        var writer = _host.Client(await _host.KeyAsync("collection:*:read", "collection:*:maintain"));
        await writer.PostAsync("/v1/entries", Json(Create("team", "Retry policy", "Retry idempotent calls with backoff.", "# Rules\n\nUse jitter.\n")));
        var key = await _host.KeyAsync("collection:*:read");
        var connection = new EffectiveConnection("personal", new Uri("http://localhost/"), new("KEY"), [CollectionId.Parse("team")], TimeSpan.FromSeconds(5), ForwardPolicy.Nothing);
        using var provider = new RemoteHttpProvider(connection, key, "space-game", _host.Handler);

        var search = await provider.SearchAsync(
            new SearchRequest("retry backoff", QueryContext.Empty, SearchFilters.None, Relaxation.Default, new ResponseLimits(10, 4000), InclusionOptions.Default, 20),
            CancellationToken.None);
        Assert.True(search.IsSuccess, search.Error?.Message);
        var hit = Assert.Single(search.Value.Results);
        Assert.Equal("team/retry-policy", hit.Entry.Ref.ToString());
        Assert.Equal("personal", hit.SourceId);

        var get = await provider.GetAsync(new GetRequest(EntryRef.Parse("team/retry-policy#rules"), 4000, false), CancellationToken.None);
        Assert.Contains("jitter", get.Value.Body, StringComparison.Ordinal);

        var list = await provider.ListAsync(new ListRequest(SearchFilters.None, InclusionOptions.Default, false, null, 10), CancellationToken.None);
        Assert.Contains(list.Value.Items, i => i.Entry.Ref.ToString() == "team/retry-policy");

        var missing = await provider.GetAsync(new GetRequest(EntryRef.Parse("team/nope"), 4000, false), CancellationToken.None);
        Assert.Equal(Application.Common.ErrorCode.NotFound, missing.Error!.Code);
    }

    [Fact]
    public async Task Only_allowed_context_is_forwarded()
    {
        var key = await _host.KeyAsync("collection:*:read");
        var capture = new CapturingHandler(_host.Handler);
        var context = new QueryContext("space-game", ["unity"], new Dictionary<string, string> { ["unity"] = "2022.3" }, ["Assets/Secret/Plan.cs"], ["SecretSymbol"]);
        var request = new SearchRequest("anything", context, SearchFilters.None, Relaxation.Default, new ResponseLimits(10, 4000), InclusionOptions.Default, 20);

        var nothing = new EffectiveConnection("p", new Uri("http://localhost/"), new("KEY"), [], TimeSpan.FromSeconds(5), ForwardPolicy.Nothing);
        using (var provider = new RemoteHttpProvider(nothing, key, null, capture))
        {
            await provider.SearchAsync(request, CancellationToken.None);
        }

        Assert.DoesNotContain("space-game", capture.LastBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Assets/Secret", capture.LastBody, StringComparison.Ordinal);
        Assert.DoesNotContain("SecretSymbol", capture.LastBody, StringComparison.Ordinal);

        var technologies = nothing with { Forward = new ForwardPolicy(false, true, false, false) };
        using (var provider = new RemoteHttpProvider(technologies, key, null, capture))
        {
            await provider.SearchAsync(request, CancellationToken.None);
        }

        Assert.Contains("unity", capture.LastBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Assets/Secret", capture.LastBody, StringComparison.Ordinal);
    }

    private sealed class CapturingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public string LastBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            return await base.SendAsync(request, cancellationToken);
        }
    }
}

public sealed class BehindProxyTests : IAsyncLifetime
{
    private TestHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await TestHost.StartAsync("behind-proxy");

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Plain_http_api_requests_are_refused_behind_a_proxy()
    {
        var client = _host.Client(await _host.KeyAsync("collection:*:read"));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/v1/collections")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }
}
