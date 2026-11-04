using System.Diagnostics;
using System.Globalization;
using AgentBrain.Application.Indexing;
using AgentBrain.Composition;
using AgentBrain.Contracts.Rendering;
using AgentBrain.Infrastructure.Local.Configuration;
using SearchInput = AgentBrain.Application.Operations.Discovery.SearchInput;

namespace AgentBrain.Evaluation;

/// <summary>
/// Measures index cost at the A12 envelope (plan §5.14): a cold rebuild, a no-change incremental refresh
/// (the per-search hot path), a refresh after one edit, and a search. Synthetic entries, recorded as
/// baselines, not SLAs.
/// </summary>
internal static class ScanBenchmark
{
    private static readonly string[] Words =
    [
        "save", "slot", "render", "pipeline", "input", "binding", "network", "retry", "cache", "asset", "level", "loading",
        "physics", "layer", "inventory", "item", "shader", "audio", "mixer", "dialogue", "camera", "animation", "build", "test",
    ];

    public static async Task<int> RunAsync(IReadOnlyList<int> sizes)
    {
        Console.WriteLine("entries · rebuild ms · no-change refresh ms · one-edit refresh ms · search ms");
        foreach (var size in sizes)
        {
            var workspace = Directory.CreateTempSubdirectory("brain-scan-").FullName;
            try
            {
                var collection = Path.Combine(workspace, "kb");
                Generate(collection, size);
                var project = Directory.CreateDirectory(Path.Combine(workspace, "project", ".agent-brain")).FullName;
                await File.WriteAllTextAsync(Path.Combine(project, "config.json"),
                    $$"""{ "schema": 1, "project": { "id": "bench" }, "collections": { "bench.kb": { "path": "{{collection.Replace('\\', '/')}}" } } }""");
                var environment = new HostEnvironment(name => name == ConfigLocations.ConfigDirVariable ? Path.Combine(workspace, "user") : null, OperatingSystem.IsWindows(), workspace);
                var opened = await BrainRuntime.OpenAsync(new RuntimeOptions(Path.GetDirectoryName(project)!) { Environment = environment }, CancellationToken.None);
                using var runtime = opened.Value;

                var rebuild = await Time(() => runtime.Index.SynchronizeAsync(SyncMode.Rebuild, CancellationToken.None).AsTask());
                var noChange = await Time(() => runtime.Index.SynchronizeAsync(SyncMode.Incremental, CancellationToken.None).AsTask());
                var target = Path.Combine(collection, "entries", "entry-0.md");
                await File.AppendAllTextAsync(target, "\nEdited.\n");
                File.SetLastWriteTimeUtc(target, DateTime.UtcNow.AddSeconds(5));
                var oneEdit = await Time(() => runtime.Index.SynchronizeAsync(SyncMode.Incremental, CancellationToken.None).AsTask());
                var search = await Time(() => runtime.Search.ExecuteAsync(new SearchInput("save slot input binding"), TextRenderer.Compact, CancellationToken.None).AsTask());

                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{size,7} · {rebuild,10:0} · {noChange,20:0.0} · {oneEdit,19:0.0} · {search,9:0.0}"));
            }
            finally
            {
                Directory.Delete(workspace, recursive: true);
            }
        }

        return 0;
    }

    private static async Task<double> Time(Func<Task> action)
    {
        var stopwatch = Stopwatch.StartNew();
        await action();
        return stopwatch.Elapsed.TotalMilliseconds;
    }

    private static void Generate(string collection, int size)
    {
        var entries = Directory.CreateDirectory(Path.Combine(collection, "entries")).FullName;
        File.WriteAllText(Path.Combine(collection, "collection.json"),
            """{ "schema": 1, "id": "bench.kb", "title": "Benchmark", "defaults": { "applies": { "scope": "general" } } }""");
        var random = new Random(42);
        for (var i = 0; i < size; i++)
        {
            string Pick() => Words[random.Next(Words.Length)];
            var title = $"{Pick()} {Pick()} note {i}";
            var body = string.Join(' ', Enumerable.Range(0, 120).Select(_ => Pick()));
            File.WriteAllText(Path.Combine(entries, $"entry-{i}.md"),
                $"---\nschema: 1\nid: entry-{i}\nkind: knowledge\ntitle: \"{title}\"\nsummary: \"About {Pick()} and {Pick()}.\"\ntags: [{Pick()}, {Pick()}]\n---\n# {title}\n\n{body}\n");
        }
    }
}
