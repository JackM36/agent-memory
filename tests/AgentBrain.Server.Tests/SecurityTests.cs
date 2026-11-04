using System.Net;
using System.Text;

namespace AgentBrain.Server.Tests;

/// <summary>Phase 6 security checks against the HTTP surface (plan §9.7).</summary>
public sealed class SecurityTests : IAsyncLifetime
{
    private TestHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await TestHost.StartAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Theory]
    [InlineData("/v1/entries/..%2F..%2Fdb/brain")]
    [InlineData("/v1/entries/notes/..%2Fops")]
    [InlineData("/v1/entries/notes/ops%2500")]
    [InlineData("/v1/entries/notes/ops%5C..%5C..%5Chost.json")]
    [InlineData("/v1/entries/NOTES/ops")]
    [InlineData("/v1/entries/notes/ops?section=..%2F..")]
    [InlineData("/v1/proposals/..%2F..%2Fhost")]
    public async Task Traversal_and_malformed_identities_are_rejected_without_touching_files(string path)
    {
        var client = _host.Client(await _host.KeyAsync("collection:*:read"));
        var response = await client.GetAsync(path);
        Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound, $"{path}: {(int)response.StatusCode}");
        Assert.DoesNotContain("brain.db", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Keys_never_appear_in_responses()
    {
        var key = await _host.KeyAsync("collection:*:read");
        var wrong = key[..^4] + "aaaa";

        var rejected = await _host.Client(wrong).GetAsync("/v1/collections");
        var accepted = await _host.Client(key).PostAsync("/v1/search", new StringContent("""{ "query": "restart" }""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        foreach (var body in new[] { await rejected.Content.ReadAsStringAsync(), await accepted.Content.ReadAsStringAsync() })
        {
            Assert.DoesNotContain(key.Split('_')[2], body, StringComparison.Ordinal);
            Assert.DoesNotContain(wrong.Split('_')[2], body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Oversized_requests_are_refused()
    {
        var client = _host.Client(await _host.KeyAsync("collection:*:read"));
        var query = new string('a', 2 * 1024 * 1024);
        var response = await client.PostAsync("/v1/search", new StringContent($$"""{ "query": "{{query}}" }""", Encoding.UTF8, "application/json"));
        Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge, ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Long_queries_are_validation_errors()
    {
        var client = _host.Client(await _host.KeyAsync("collection:*:read"));
        var response = await client.PostAsync("/v1/search", new StringContent($$"""{ "query": "{{new string('a', 1500)}}" }""", Encoding.UTF8, "application/json"));
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
    }
}
