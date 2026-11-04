using System.CommandLine;
using System.Text.Json;
using AgentBrain.Cli.Infrastructure;

namespace AgentBrain.Cli.Tests;

/// <summary>
/// End to end through the command tree against a copy of the evaluation corpus: configuration discovery,
/// Markdown parsing, indexing, ranking, budgeting and rendering, with exit codes as documented.
/// </summary>
[Collection("cli")]
public sealed class CommandLineTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("brain-cli-").FullName;
    private readonly string _project;

    public CommandLineTests()
    {
        _project = Path.Combine(_workspace, "game");
        var corpus = FindCorpus();
        foreach (var collection in new[] { "general.engineering", "unity.kb", "space-game.memory" })
        {
            Copy(Path.Combine(corpus, collection), Path.Combine(_project, ".agent-brain", "kb", collection));
        }

        File.WriteAllText(Path.Combine(_project, ".agent-brain", "config.json"), """
            {
              "schema": 1,
              "project": { "id": "space-game", "technologies": ["unity", "csharp"], "packages": { "com.unity.entities": "1.2.4" } },
              "collections": {
                "general.engineering": { "path": "kb/general.engineering" },
                "unity.kb": { "path": "kb/unity.kb" },
                "space-game.memory": { "path": "kb/space-game.memory", "capabilities": ["read", "maintain"] }
              }
            }
            """);
        Directory.CreateDirectory(Path.Combine(_workspace, "user"));
        Environment.SetEnvironmentVariable("AGENT_BRAIN_CONFIG_DIR", Path.Combine(_workspace, "user"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("AGENT_BRAIN_CONFIG_DIR", null);
        try
        {
            Directory.Delete(_workspace, recursive: true);
        }
        catch (IOException)
        {
            // An index connection may still be closing; the OS cleans temp.
        }
    }

    private async Task<(int Exit, string Out, string Error)> RunAsync(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var io = new CliIo(output, error, OutputIsTerminal: false, _project);
        var exit = await BrainCommandLine.Create(io).Parse(args).InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public void Version_prints_the_product_version()
    {
        using var output = new StringWriter();
        var exitCode = BrainCommandLine.Create(new CliIo(output, TextWriter.Null, false, _project)).Parse(["--version"]).Invoke(new InvocationConfiguration { Output = output });

        Assert.Equal(0, exitCode);
        Assert.StartsWith("0.1.0", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_commands_are_usage_errors() =>
        Assert.NotEmpty(BrainCommandLine.Create().Parse(["frobnicate"]).Errors);

    [Fact]
    public async Task Search_returns_compact_results_and_the_blocking_question()
    {
        var (exit, output, _) = await RunAsync("search", "change how save slots are written", "--file", "Assets/Scripts/Save/SaveSystem.cs");

        Assert.Equal(0, exit);
        var lines = output.Split('\n');
        Assert.StartsWith("brain r1 ·", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("BLOCKING space-game.memory/save-format-v2-undecided", lines[1], StringComparison.Ordinal);
        Assert.Contains("1 space-game.memory/save-system [memory·this project·", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Json_search_output_is_a_valid_contract()
    {
        var (exit, output, _) = await RunAsync("search", "entities baking", "--format", "json");

        Assert.Equal(0, exit);
        using var json = JsonDocument.Parse(output);
        Assert.Equal(1, json.RootElement.GetProperty("protocol").GetInt32());
        Assert.Equal("unity.kb/entities-baking", json.RootElement.GetProperty("results")[0].GetProperty("ref").GetString());
        Assert.True(json.RootElement.GetProperty("budget").GetProperty("usedBytes").GetInt32() <= 4000);
    }

    [Fact]
    public async Task Get_returns_a_section_with_provenance()
    {
        var (exit, output, _) = await RunAsync("get", "unity.kb/serialize-reference#pitfalls");

        Assert.Equal(0, exit);
        Assert.Contains("── entry unity.kb/serialize-reference · knowledge", output, StringComparison.Ordinal);
        Assert.Contains("## Pitfalls", output, StringComparison.Ordinal);
        Assert.DoesNotContain("## Managed references", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("unity.kb/serialize-reference#no-such-section", 3)]
    [InlineData("unity.kb/missing", 3)]
    [InlineData("not a ref", 2)]
    public async Task Get_failures_map_to_exit_codes(string target, int exitCode)
    {
        var (exit, _, _) = await RunAsync("get", target);
        Assert.Equal(exitCode, exit);
    }

    [Fact]
    public async Task Truncation_is_explicit()
    {
        var (exit, output, _) = await RunAsync("get", "unity.kb/serialize-reference", "--max-bytes", "200");

        Assert.Equal(0, exit);
        Assert.Contains("── truncated:", output, StringComparison.Ordinal);
        Assert.Contains("#pitfalls", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_budget_below_the_minimum_exits_with_7()
    {
        var (exit, output, _) = await RunAsync("search", "save", "--max-bytes", "10");
        Assert.Equal(7, exit);
        Assert.StartsWith("error budget_too_small", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_pages_through_everything_and_blocking_lists_questions()
    {
        var (_, all, _) = await RunAsync("list", "--collection", "space-game.memory", "--page-size", "100", "--include-archived");
        Assert.Equal(11, all.Split('\n').Count(l => l.StartsWith("- ", StringComparison.Ordinal)));

        var (_, blocking, _) = await RunAsync("list", "--blocking");
        Assert.Contains("space-game.memory/save-format-v2-undecided", blocking, StringComparison.Ordinal);
        Assert.Equal(1, blocking.Split('\n').Count(l => l.StartsWith("- ", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task References_and_backlinks_resolve_by_identity()
    {
        var (_, refs, _) = await RunAsync("refs", "space-game.memory/save-system");
        Assert.Contains("space-game.memory/save-format-v2-undecided \"the open question\"", refs, StringComparison.Ordinal);
        Assert.DoesNotContain(" — ", refs, StringComparison.Ordinal);

        var (_, backlinks, _) = await RunAsync("backlinks", "general.engineering/guard-clauses");
        Assert.Contains("general.engineering/small-functions", backlinks, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Links_use_the_canonical_anchor()
    {
        var (exit, output, _) = await RunAsync("link", "unity.kb/serialize-reference", "--section", "Managed References");
        Assert.Equal(0, exit);
        Assert.Equal("[unity.kb/serialize-reference#managed-references](entry://unity.kb/serialize-reference#managed-references)", output.Trim());
    }

    [Fact]
    public async Task Index_status_reports_entries_and_config_show_reports_origins()
    {
        var (exit, status, _) = await RunAsync("index", "reindex");
        Assert.Equal(0, exit);
        Assert.Contains("index · 32 entries", status, StringComparison.Ordinal);

        var (_, config, _) = await RunAsync("config", "show", "--origin");
        Assert.Contains("project.id = space-game    [Project", config, StringComparison.Ordinal);
        Assert.Contains("search.maxResults = 10    [BuiltIn]", config, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Edits_are_picked_up_by_the_next_search()
    {
        await RunAsync("search", "warmup");
        var file = Path.Combine(_project, ".agent-brain", "kb", "space-game.memory", "memory", "hud-ui-toolkit.md");
        await File.WriteAllTextAsync(file, (await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken)).Replace("UI Toolkit", "Zephyrwidgets", StringComparison.Ordinal), TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));

        var (_, output, _) = await RunAsync("search", "zephyrwidgets");

        Assert.Contains("space-game.memory/hud-ui-toolkit", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deletions_and_moves_update_the_index()
    {
        await RunAsync("search", "warmup");
        var memory = Path.Combine(_project, ".agent-brain", "kb", "space-game.memory", "memory");
        File.Delete(Path.Combine(memory, "hud-ui-toolkit.md"));
        Directory.CreateDirectory(Path.Combine(memory, "moved"));
        File.Move(Path.Combine(memory, "inventory-items.md"), Path.Combine(memory, "moved", "inventory-items.md"));

        var (_, search, _) = await RunAsync("search", "HUD UI Toolkit menus");
        Assert.DoesNotContain("hud-ui-toolkit", search, StringComparison.Ordinal);

        var (exit, get, _) = await RunAsync("get", "space-game.memory/inventory-items");
        Assert.Equal(0, exit);
        Assert.Contains("ItemDatabase", get, StringComparison.Ordinal);

        var (_, list, _) = await RunAsync("list", "--collection", "space-game.memory", "--format", "json");
        Assert.Contains("memory/moved/inventory-items.md", list, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Batch_get_reports_each_failure_and_exits_with_the_first_failure_code()
    {
        var (exit, output, _) = await RunAsync("get", "unity.kb/serialize-reference", "unity.kb/missing");

        Assert.Equal(3, exit);
        Assert.Contains("── entry unity.kb/serialize-reference", output, StringComparison.Ordinal);
        Assert.Contains("── failed unity.kb/missing: not_found", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_opt_in_query_log_records_searches()
    {
        var config = Path.Combine(_project, ".agent-brain", "config.json");
        await File.WriteAllTextAsync(config, (await File.ReadAllTextAsync(config, TestContext.Current.CancellationToken))
            .Replace("\"collections\":", "\"search\": { \"log\": true },\n  \"collections\":", StringComparison.Ordinal), TestContext.Current.CancellationToken);

        await RunAsync("search", "entities baking");

        var log = Path.Combine(_project, ".agent-brain", "local", "state", "query-log.jsonl");
        var line = Assert.Single(await File.ReadAllLinesAsync(log, TestContext.Current.CancellationToken));
        using var json = JsonDocument.Parse(line);
        Assert.Equal("entities baking", json.RootElement.GetProperty("query").GetString());
        Assert.Equal("unity.kb/entities-baking", json.RootElement.GetProperty("results")[0].GetString());
    }

    [Fact]
    public async Task Extra_collection_roots_are_searchable_for_one_call()
    {
        var web = Path.Combine(FindCorpus(), "web.kb");
        var (exit, output, _) = await RunAsync("search", "react useEffect dependency", "--collection-root", web);
        Assert.Equal(0, exit);
        Assert.Contains("web.kb/react-effect-deps", output, StringComparison.Ordinal);
    }

    private static void Copy(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static string FindCorpus()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "eval", "corpus");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("eval/corpus not found.");
    }
}
