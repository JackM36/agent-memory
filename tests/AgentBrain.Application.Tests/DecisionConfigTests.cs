using AgentBrain.Application.Configuration;
using AgentBrain.Application.Decisions;

namespace AgentBrain.Application.Tests;

/// <summary>Configuration of the decision ports (ADR 0010): defaults, layering, validation and privacy.</summary>
public sealed class DecisionConfigTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "brain-decision-tests", "game"));
    private static readonly string ConfigDir = Path.Combine(Root, ".agent-brain");
    private static readonly string UserDir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "brain-decision-tests", "user"));
    private static readonly ProjectLocation Project = new(Root, ConfigDir);
    private static readonly ConfigDocument MinimalProject = new() { Project = new ProjectSection("space-game") };

    private static LoadedConfigDocument User(ConfigDocument d) => new(ConfigLevel.User, Path.Combine(UserDir, "config.json"), UserDir, d);

    private static LoadedConfigDocument ProjectFile(ConfigDocument d) => new(ConfigLevel.Project, Path.Combine(ConfigDir, "config.json"), ConfigDir, d);

    private static ConfigResolution Resolve(params LoadedConfigDocument[] documents) =>
        ConfigLayering.Resolve(new ConfigurationInput(Project, documents));

    [Fact]
    public void Every_port_defaults_to_the_heuristic_and_no_model_is_used()
    {
        var resolution = Resolve(ProjectFile(MinimalProject));

        Assert.False(resolution.HasErrors, string.Join("; ", resolution.Diagnostics));
        var d = resolution.Configuration.Decisions;
        Assert.Equal(DecisionMode.Heuristic, d.Relevance);
        Assert.Equal(DecisionMode.Heuristic, d.Classifier);
        Assert.Equal(DecisionMode.Heuristic, d.Duplicates);
        Assert.False(d.CaptureGate);
        Assert.False(d.UsesModel);
        Assert.Equal(DecisionDefaults.ApiKeyEnv, d.Jev.Credential.EnvironmentVariable);
        Assert.Equal(DecisionDefaults.Model, d.Jev.Model);
        Assert.False(d.Jev.IsAlias);
    }

    [Fact]
    public void Ports_are_chosen_independently_and_the_project_overrides_the_user()
    {
        var user = User(new ConfigDocument { Decisions = new DecisionsSection(Relevance: "hybrid", Classifier: "jev", Duplicates: "jev") });
        var project = ProjectFile(MinimalProject with { Decisions = new DecisionsSection(Classifier: "heuristic", CaptureGate: "jev") });

        var d = Resolve(user, project).Configuration.Decisions;

        Assert.Equal(DecisionMode.Hybrid, d.Relevance);
        Assert.Equal(DecisionMode.Heuristic, d.Classifier);
        Assert.Equal(DecisionMode.Jev, d.Duplicates);
        Assert.True(d.CaptureGate);
        Assert.True(d.UsesModel);
    }

    [Fact]
    public void Jev_settings_are_read_and_the_key_is_a_variable_name()
    {
        var jev = new JevSection("MY_KEY", "jev-1.13.0", 900, 4, 20, 0.8, 50);
        var d = Resolve(ProjectFile(MinimalProject with { Decisions = new DecisionsSection(jev, Relevance: "jev") })).Configuration.Decisions;

        Assert.Equal("MY_KEY", d.Jev.Credential.EnvironmentVariable);
        Assert.Equal("jev-1.13.0", d.Jev.Model);
        Assert.Equal(TimeSpan.FromMilliseconds(900), d.Jev.Timeout);
        Assert.Equal(4, d.Jev.MaxConcurrency);
        Assert.Equal(20, d.Jev.MaxCallsPerOperation);
        Assert.Equal(0.8, d.Jev.MinConfidence);
        Assert.Equal(50, d.Jev.RelevancePool);
    }

    [Theory]
    [InlineData("sk-abc 123")]
    [InlineData("a-b")]
    [InlineData("")]
    public void A_key_value_in_place_of_a_variable_name_is_an_error(string text)
    {
        var resolution = Resolve(ProjectFile(MinimalProject with { Decisions = new DecisionsSection(new JevSection(ApiKeyEnv: text)) }));

        Assert.True(resolution.HasErrors);
        Assert.Contains(resolution.Diagnostics, d => d.Path == "decisions.jev.apiKeyEnv");
    }

    [Theory]
    [InlineData("relevance", "maybe")]
    [InlineData("classifier", "hybrid")]
    [InlineData("duplicates", "hybrid")]
    [InlineData("captureGate", "hybrid")]
    public void Unknown_or_unsupported_modes_are_errors(string port, string value)
    {
        var section = port switch
        {
            "relevance" => new DecisionsSection(Relevance: value),
            "classifier" => new DecisionsSection(Classifier: value),
            "duplicates" => new DecisionsSection(Duplicates: value),
            _ => new DecisionsSection(CaptureGate: value),
        };

        var resolution = Resolve(ProjectFile(MinimalProject with { Decisions = section }));

        Assert.True(resolution.HasErrors);
        Assert.Contains(resolution.Diagnostics, d => d.Path == $"decisions.{port}");
    }

    [Fact]
    public void Out_of_range_limits_are_errors()
    {
        var jev = new JevSection(TimeoutMs: 5, MaxConcurrency: 0, MaxCallsPerOperation: 5000, MinConfidence: 1.5, RelevancePool: 0);
        var resolution = Resolve(ProjectFile(MinimalProject with { Decisions = new DecisionsSection(jev) }));

        var paths = resolution.Diagnostics.Select(d => d.Path).ToList();
        Assert.Contains("decisions.jev.timeoutMs", paths);
        Assert.Contains("decisions.jev.maxConcurrency", paths);
        Assert.Contains("decisions.jev.maxCallsPerOperation", paths);
        Assert.Contains("decisions.jev.minConfidence", paths);
        Assert.Contains("decisions.jev.relevancePool", paths);
    }

    [Theory]
    [InlineData("jev-latest")]
    [InlineData("jev-preview")]
    public void A_moving_alias_is_warned_about_only_when_a_model_is_used(string model)
    {
        var jev = new JevSection(Model: model);
        var used = Resolve(ProjectFile(MinimalProject with { Decisions = new DecisionsSection(jev, Relevance: "jev") }));
        var unused = Resolve(ProjectFile(MinimalProject with { Decisions = new DecisionsSection(jev) }));

        Assert.False(used.HasErrors);
        Assert.Contains(used.Diagnostics, d => d.Path == "decisions.jev.model" && d.Severity == AgentBrain.Domain.Validation.IssueSeverity.Warning);
        Assert.DoesNotContain(unused.Diagnostics, d => d.Path == "decisions.jev.model");
    }

    [Fact]
    public void External_decisions_are_off_for_every_collection_unless_registered_with_the_flag()
    {
        var collections = new Dictionary<string, CollectionRegistration>
        {
            ["library"] = new("lib", ["read", "maintain"], AllowExternalDecisions: true),
            ["space-game.memory"] = new("mem", ["read", "maintain"]),
        };

        var resolved = Resolve(ProjectFile(MinimalProject with { Collections = collections })).Configuration.Collections;

        Assert.True(resolved.Single(c => c.Id.Value == "library").AllowExternalDecisions);
        Assert.False(resolved.Single(c => c.Id.Value == "space-game.memory").AllowExternalDecisions);
    }
}
