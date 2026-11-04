using System.Collections.Concurrent;
using System.CommandLine;
using AgentBrain.Application.Authorization;
using AgentBrain.Application.Common;
using AgentBrain.Application.Decisions;
using AgentBrain.Application.Operations.Discovery;
using AgentBrain.Application.Writing;
using AgentBrain.Cli.Infrastructure;
using AgentBrain.Composition;
using AgentBrain.Contracts.Rendering;
using AgentBrain.Domain.Entries;
using AgentBrain.Infrastructure.Local.Configuration;

namespace AgentBrain.Cli.Tests;

/// <summary>
/// The decision ports wired through the real runtime (ADR 0010), against a scripted decision model. No test here
/// contacts a network service: the model is a fake, or the key is absent and the runtime must stay on heuristics.
/// </summary>
[Collection("cli")]
public sealed class DecisionRuntimeTests : IDisposable
{
    private const string Query = "change how save slots are written";
    private const string SaveSystem = "space-game.memory/save-system";

    private readonly string _workspace = Directory.CreateTempSubdirectory("brain-decisions-").FullName;
    private readonly string _project;
    private readonly string _user;

    public DecisionRuntimeTests()
    {
        _project = Path.Combine(_workspace, "game");
        _user = Directory.CreateDirectory(Path.Combine(_workspace, "user")).FullName;
        var corpus = FindCorpus();
        foreach (var collection in new[] { "general.engineering", "unity.kb", "space-game.memory" })
        {
            Copy(Path.Combine(corpus, collection), Path.Combine(_project, ".agent-brain", "kb", collection));
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspace, recursive: true);
        }
        catch (IOException)
        {
            // An index connection may still be closing; the OS cleans temp.
        }
    }

    /// <summary>A model that answers from a script and remembers every request it was sent.</summary>
    private sealed class ScriptedModel(Func<DecisionRequest, Result<DecisionResponse>> script) : IDecisionModel
    {
        public ConcurrentBag<DecisionRequest> Requests { get; } = [];

        public ValueTask<Result<DecisionResponse>> AskAsync(DecisionRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return ValueTask.FromResult(script(request));
        }
    }

    private static Result<DecisionResponse> Noul(double value, string name = "answers") =>
        new DecisionResponse(new Dictionary<string, DecisionAnswer> { [name] = new NoulAnswer(value) }, 100);

    private static Result<DecisionResponse> Unavailable() => new Error(ErrorCode.ProviderUnavailable, "decision model unavailable: timed out after 1500 ms");

    private void WriteConfig(string decisions, bool allowMemory = true, bool allowKnowledge = true)
    {
        var memory = allowMemory ? ", \"allowExternalDecisions\": true" : "";
        var knowledge = allowKnowledge ? ", \"allowExternalDecisions\": true" : "";
        File.WriteAllText(Path.Combine(_project, ".agent-brain", "config.json"), $$"""
            {
              "schema": 1,
              "project": { "id": "space-game", "technologies": ["unity", "csharp"] },
              "collections": {
                "general.engineering": { "path": "kb/general.engineering"{{knowledge}} },
                "unity.kb": { "path": "kb/unity.kb"{{knowledge}} },
                "space-game.memory": { "path": "kb/space-game.memory", "capabilities": ["read", "maintain"]{{memory}} }
              },
              "writes": { "memory": "space-game.memory" }
              {{decisions}}
            }
            """);
    }

    private static HostEnvironment Isolated(string userDirectory, string? key = null) =>
        new(name => name == ConfigLocations.ConfigDirVariable ? userDirectory : name == DecisionDefaults.ApiKeyEnv ? key : null, OperatingSystem.IsWindows(), userDirectory);

    private async Task<BrainRuntime> OpenAsync(IDecisionModel? model = null, string? key = null, DecisionMeter? meter = null)
    {
        var opened = await BrainRuntime.OpenAsync(
            new RuntimeOptions(_project) { Environment = Isolated(_user, key), DecisionModel = model, DecisionMeter = meter }, TestContext.Current.CancellationToken);
        return opened.IsSuccess ? opened.Value : throw new InvalidOperationException(opened.Error.ToString());
    }

    private static async Task<SearchResponseView> SearchAsync(BrainRuntime runtime, string query = Query)
    {
        var input = new SearchInput(query) { Files = ["Assets/Scripts/Save/SaveSystem.cs"], Explain = true };
        var response = await runtime.Search.ExecuteAsync(input, TextRenderer.Compact, TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccess, response.IsSuccess ? "" : response.Error.ToString());
        return new SearchResponseView(
            [.. response.Value.Results.Select(r => r.Entry.Ref.ToString())],
            [.. response.Value.Blocking.Select(r => r.Entry.Ref.ToString())],
            response.Value.Outcome.Notes ?? []);
    }

    private sealed record SearchResponseView(IReadOnlyList<string> Results, IReadOnlyList<string> Blocking, IReadOnlyList<string> Notes);

    [Fact]
    public async Task With_nothing_configured_every_port_is_a_heuristic_and_no_model_is_asked()
    {
        WriteConfig("");
        var model = new ScriptedModel(_ => Noul(1));
        using var runtime = await OpenAsync(model);

        var search = await SearchAsync(runtime);

        Assert.Contains(SaveSystem, search.Results);
        Assert.Empty(search.Notes);
        Assert.Empty(model.Requests);
        Assert.All(runtime.Decisions.Status().Ports, p => Assert.False(p.UsesModel));
    }

    [Fact]
    public async Task Hybrid_relevance_vetoes_an_entry_the_heuristic_admitted_and_reports_the_model_in_the_explanation()
    {
        WriteConfig(""", "decisions": { "relevance": "hybrid" }""");
        var model = new ScriptedModel(r => Noul(r.State.Contains("Save system: slots", StringComparison.Ordinal) ? 0.05 : 0.5));
        using var runtime = await OpenAsync(model);

        var search = await SearchAsync(runtime);

        Assert.DoesNotContain(SaveSystem, search.Results);
        Assert.NotEmpty(model.Requests);
    }

    [Fact]
    public async Task Jev_relevance_admits_by_the_models_probability_and_orders_by_it()
    {
        WriteConfig(""", "decisions": { "relevance": "jev" }""");
        var model = new ScriptedModel(r => Noul(r.State.Contains("Save system: slots", StringComparison.Ordinal) ? 0.97 : 0.01));
        using var runtime = await OpenAsync(model);

        var search = await SearchAsync(runtime);

        Assert.Equal([SaveSystem], search.Results);
    }

    [Fact]
    public async Task A_collection_that_does_not_allow_external_decisions_is_never_sent_and_is_judged_by_the_heuristic()
    {
        WriteConfig(""", "decisions": { "relevance": "jev" }""", allowMemory: false);
        var model = new ScriptedModel(_ => Noul(0.01));
        using var runtime = await OpenAsync(model);

        var search = await SearchAsync(runtime);

        Assert.Contains(SaveSystem, search.Results);
        Assert.All(model.Requests, r => Assert.DoesNotContain("Save system: slots", r.State, StringComparison.Ordinal));
        Assert.All(model.Requests, r => Assert.DoesNotContain("space-game.memory", r.State, StringComparison.Ordinal));
    }

    [Fact]
    public async Task No_collection_allowed_means_the_model_is_not_contacted_at_all()
    {
        WriteConfig(""", "decisions": { "relevance": "jev" }""", allowMemory: false, allowKnowledge: false);
        var model = new ScriptedModel(_ => Noul(0.01));
        using var runtime = await OpenAsync(model);

        var search = await SearchAsync(runtime);

        Assert.Contains(SaveSystem, search.Results);
        Assert.Empty(model.Requests);
        Assert.Contains(runtime.Decisions.Status().Problems, p => p.Contains("allowExternalDecisions", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Entry_bodies_are_never_sent_to_the_relevance_judge()
    {
        WriteConfig(""", "decisions": { "relevance": "hybrid" }""");
        var model = new ScriptedModel(_ => Noul(0.5));
        using var runtime = await OpenAsync(model);

        await SearchAsync(runtime);

        Assert.NotEmpty(model.Requests);
        Assert.All(model.Requests, r => Assert.DoesNotContain("slot-N.json.tmp", r.State, StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unavailable_model_falls_back_to_the_heuristic_with_a_note()
    {
        WriteConfig("");
        using var plain = await OpenAsync();
        var expected = await SearchAsync(plain);
        WriteConfig(""", "decisions": { "relevance": "jev" }""");
        using var runtime = await OpenAsync(new ScriptedModel(_ => Unavailable()));

        var search = await SearchAsync(runtime);

        Assert.Equal(expected.Results, search.Results);
        Assert.Equal(expected.Blocking, search.Blocking);
        Assert.Contains(search.Notes, n => n.Contains("relevance judge unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_missing_key_keeps_heuristics_and_says_so_without_any_network_call()
    {
        WriteConfig(""", "decisions": { "relevance": "hybrid", "classifier": "jev" }""");
        using var runtime = await OpenAsync(model: null, key: null);

        var search = await SearchAsync(runtime);

        Assert.Contains(SaveSystem, search.Results);
        Assert.Contains(runtime.Diagnostics, d => d.Path == "decisions" && d.Message.Contains(DecisionDefaults.ApiKeyEnv, StringComparison.Ordinal));
        Assert.False(runtime.Decisions.Status().KeySet);
    }

    [Fact]
    public async Task The_classifier_and_duplicate_judge_annotate_a_save_but_never_change_it()
    {
        WriteConfig(""", "decisions": { "classifier": "jev", "duplicates": "jev" }""");
        var model = new ScriptedModel(r => r.Questions.ContainsKey("kind")
            ? new DecisionResponse(
                new Dictionary<string, DecisionAnswer>
                {
                    ["kind"] = new ChoiceAnswer("knowledge", 0.93, new Dictionary<string, double> { ["knowledge"] = 0.93 }),
                    ["none_fit"] = new NoulAnswer(0.02),
                    ["technology_specific"] = new NoulAnswer(0.9),
                },
                100)
            : new DecisionResponse(
                new Dictionary<string, DecisionAnswer> { ["relation"] = new ChoiceAnswer("duplicate", 0.9, new Dictionary<string, double> { ["duplicate"] = 0.9 }) },
                100));
        using var runtime = await OpenAsync(model);
        var entry = new NewEntry(EntryKind.Memory, "Save slots are written atomically", "SaveSystem writes slot files through a temp file and renames them.", "Details.");

        var written = await runtime.Maintenance.CreateAsync(CallerContext.Local("test"), null, entry, [], TestContext.Current.CancellationToken);

        Assert.True(written.IsSuccess, written.IsSuccess ? "" : written.Error.ToString());
        Assert.Contains(written.Value.Notes ?? [], n => n.Contains("suggests 'knowledge' instead of 'memory'", StringComparison.Ordinal));
        Assert.Contains(written.Value.Similar, s => s.Verdict == DuplicateVerdict.Duplicate);
        var stored = await runtime.Get.ExecuteAsync([written.Value.Ref], null, false, TestContext.Current.CancellationToken);
        Assert.True(stored.IsSuccess);
        Assert.Contains("· memory", TextRenderer.Compact.RenderEntries(stored.Value.Entries, stored.Value.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_secret_in_a_query_is_never_sent_and_search_falls_back_with_a_note()
    {
        WriteConfig(""", "decisions": { "relevance": "hybrid" }""");
        var model = new ScriptedModel(_ => Noul(0.5));
        using var runtime = await OpenAsync(model);

        var search = await SearchAsync(runtime, Query + " token ghp_abcdefghijklmnopqrstuvwxyz0123456789");

        Assert.Empty(model.Requests);
        Assert.Contains(search.Notes, n => n.Contains("secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_entry_that_fails_the_secret_scan_is_rejected_before_any_model_is_asked()
    {
        WriteConfig(""", "decisions": { "classifier": "jev", "duplicates": "jev" }""");
        var model = new ScriptedModel(_ => Noul(0.5));
        using var runtime = await OpenAsync(model);
        var entry = new NewEntry(EntryKind.Memory, "Save slots", "How slots are saved.", "api_key = abcdefghijklmnop1234567890");

        var written = await runtime.Maintenance.CreateAsync(CallerContext.Local("test"), null, entry, [], TestContext.Current.CancellationToken);

        Assert.False(written.IsSuccess);
        Assert.Equal(ErrorCode.SecretDetected, written.Error.Code);
        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task The_meter_counts_what_the_runtime_asked()
    {
        WriteConfig(""", "decisions": { "relevance": "hybrid" }""");
        var meter = new DecisionMeter();
        using var runtime = await OpenAsync(new ScriptedModel(_ => Noul(0.5)), meter: meter);

        await SearchAsync(runtime);

        Assert.True(meter.Calls > 0);
        Assert.Equal(meter.Calls * 100, meter.InputTokens);
    }

    [Fact]
    public async Task The_status_command_lists_every_port_and_never_prints_a_key()
    {
        WriteConfig(""", "decisions": { "relevance": "hybrid" }""");
        Environment.SetEnvironmentVariable("AGENT_BRAIN_CONFIG_DIR", _user);
        Environment.SetEnvironmentVariable(DecisionDefaults.ApiKeyEnv, "sk-test-secret-value");
        try
        {
            using var output = new StringWriter();
            var io = new CliIo(output, TextWriter.Null, OutputIsTerminal: false, _project);
            var exit = await BrainCommandLine.Create(io).Parse(["decisions", "status"]).InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(0, exit);
            var text = output.ToString();
            Assert.Contains("relevance    hybrid", text, StringComparison.Ordinal);
            Assert.Contains("classifier   heuristic", text, StringComparison.Ordinal);
            Assert.Contains("TYPESAFE_API_KEY is set", text, StringComparison.Ordinal);
            Assert.DoesNotContain("sk-test-secret-value", text, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENT_BRAIN_CONFIG_DIR", null);
            Environment.SetEnvironmentVariable(DecisionDefaults.ApiKeyEnv, null);
        }
    }

    [Fact]
    public async Task Allow_and_deny_edit_only_the_flag_of_a_registered_collection()
    {
        WriteConfig("", allowMemory: false);
        Environment.SetEnvironmentVariable("AGENT_BRAIN_CONFIG_DIR", _user);
        try
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var io = new CliIo(output, error, OutputIsTerminal: false, _project);

            var allow = await BrainCommandLine.Create(io).Parse(["decisions", "allow", "space-game.memory", "--project"]).InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);
            var unknown = await BrainCommandLine.Create(io).Parse(["decisions", "allow", "nope", "--project"]).InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(0, allow);
            Assert.NotEqual(0, unknown);
            var json = File.ReadAllText(Path.Combine(_project, ".agent-brain", "config.json"));
            Assert.Contains("\"allowExternalDecisions\": true", json, StringComparison.Ordinal);
            Assert.Contains("\"path\": \"kb/space-game.memory\"", json, StringComparison.Ordinal);
            using var runtime = await OpenAsync();
            Assert.Contains("space-game.memory", runtime.Decisions.Status().AllowedCollections);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENT_BRAIN_CONFIG_DIR", null);
        }
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
