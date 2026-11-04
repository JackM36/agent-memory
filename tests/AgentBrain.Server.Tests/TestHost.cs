using System.Net.Http.Headers;
using AgentBrain.Composition;
using AgentBrain.Infrastructure.Local.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AgentBrain.Server.Tests;

/// <summary>
/// An in-process brain-server on a TestServer, with a temporary data directory: SQLite collection <c>team</c>,
/// SQLite collection <c>fast</c> with immediate publishing, and read-only Markdown collection <c>notes</c>.
/// </summary>
public sealed class TestHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private TestHost(string data, HostRuntime runtime, WebApplication app)
    {
        Data = data;
        Runtime = runtime;
        _app = app;
    }

    public string Data { get; }

    public HostRuntime Runtime { get; }

    public HttpMessageHandler Handler => _app.GetTestServer().CreateHandler();

    public static async Task<TestHost> StartAsync(string transport = "local", WatcherOptions? watcher = null)
    {
        var data = Directory.CreateTempSubdirectory("brain-server-").FullName;
        var notes = Directory.CreateDirectory(Path.Combine(data, "notes")).FullName;
        await File.WriteAllTextAsync(Path.Combine(notes, "collection.json"),
            """{ "schema": 1, "id": "notes", "title": "Notes", "readOnly": true, "defaults": { "applies": { "scope": "general" } } }""");
        await File.WriteAllTextAsync(Path.Combine(notes, "ops.md"),
            "---\nschema: 1\nid: ops\nkind: knowledge\ntitle: Operations runbook\nsummary: How to restart the ingestion service.\n---\n# Restart\n\nRun the restart script.\n\n# Escalate\n\nPage the on-call engineer.\n");
        await File.WriteAllTextAsync(Path.Combine(data, HostConfigFile.FileName), $$"""
            {
              "schema": 1,
              "transport": "{{transport}}",
              "listen": "http://127.0.0.1:5999",
              "trustedProxies": ["10.0.0.0/8"],
              "collections": {
                "team": { "title": "Team knowledge", "storage": "sqlite" },
                "fast": { "title": "Fast lane", "storage": "sqlite", "immediatePublish": true },
                "notes": { "title": "Notes", "storage": "markdown", "path": "notes", "readOnly": true }
              }
            }
            """);

        var runtime = HostRuntime.Open(data, HostRuntime.ReadConfiguration(data).Value).Value;
        var app = ServerApp.Build(runtime, b =>
        {
            b.WebHost.UseTestServer();
            if (watcher is not null)
            {
                b.Services.AddSingleton(watcher);
            }
        });
        await app.StartAsync();
        return new TestHost(data, runtime, app);
    }

    /// <summary>Creates a key through the database gate, as the server's own requests do; the watcher may be syncing.</summary>
    public async Task<string> KeyAsync(params string[] scopes)
    {
        await Runtime.Database.Gate.WaitAsync();
        try
        {
            return (await Runtime.Keys.CreateAsync("test", scopes, CancellationToken.None)).Value.Key;
        }
        finally
        {
            Runtime.Database.Gate.Release();
        }
    }

    public HttpClient Client(string? key = null)
    {
        var client = _app.GetTestClient();
        if (key is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        return client;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Runtime.Dispose();
        try
        {
            Directory.Delete(Data, recursive: true);
        }
        catch (IOException)
        {
            // SQLite may still hold the WAL file briefly on Windows.
        }
    }
}
