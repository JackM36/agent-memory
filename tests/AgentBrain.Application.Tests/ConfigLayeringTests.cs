using AgentBrain.Application.Configuration;
using AgentBrain.Application.Retrieval;
using AgentBrain.Domain.Access;
using AgentBrain.Domain.Entries;
using AgentBrain.Domain.Validation;

namespace AgentBrain.Application.Tests;

/// <summary>Acceptance checklist for configuration layering (plan §7.4, §9.2).</summary>
public sealed class ConfigLayeringTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "brain-config-tests", "game"));
    private static readonly string ConfigDir = Path.Combine(Root, ".agent-brain");
    private static readonly string UserDir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "brain-config-tests", "user"));
    private static readonly ProjectLocation Project = new(Root, ConfigDir);

    private static LoadedConfigDocument User(ConfigDocument d) => new(ConfigLevel.User, Path.Combine(UserDir, "config.json"), UserDir, d);

    private static LoadedConfigDocument ProjectFile(ConfigDocument d) => new(ConfigLevel.Project, Path.Combine(ConfigDir, "config.json"), ConfigDir, d);

    private static LoadedConfigDocument Local(ConfigDocument d) => new(ConfigLevel.ProjectLocal, Path.Combine(ConfigDir, "config.local.json"), ConfigDir, d);

    private static LoadedConfigDocument Call(ConfigDocument d) => new(ConfigLevel.Invocation, null, Root, d);

    private static readonly ConfigDocument MinimalProject = new() { Project = new ProjectSection("space-game") };

    private static ConfigResolution Resolve(params LoadedConfigDocument[] documents) =>
        ConfigLayering.Resolve(new ConfigurationInput(Project, documents));

    [Fact]
    public void Built_in_defaults_apply_when_nothing_is_set()
    {
        var resolution = Resolve(ProjectFile(MinimalProject));

        Assert.False(resolution.HasErrors, string.Join("; ", resolution.Diagnostics));
        var search = resolution.Configuration.Search;
        Assert.Equal(ConfigDefaults.MaxResults, search.MaxResults);
        Assert.Equal(Relaxation.Default, search.Relaxation);
        Assert.Equal([EntryKind.Knowledge, EntryKind.Memory, EntryKind.Uncertainty], search.Kinds);
        Assert.Equal(Path.Combine(ConfigDir, "local"), resolution.Configuration.RuntimeDirectory);
        Assert.Equal(Path.Combine(ConfigDir, "proposals"), resolution.Configuration.ProposalsDirectory);
        Assert.Equal(ConfigLevel.BuiltIn, resolution.Configuration.Origins["search.maxResults"].Level);
    }

    [Fact]
    public void Precedence_is_invocation_then_local_then_project_then_user()
    {
        static ConfigDocument Results(int n) => new() { Search = new SearchSection(MaxResults: n) };

        Assert.Equal(4, Resolve(User(Results(4)), ProjectFile(MinimalProject)).Configuration.Search.MaxResults);
        Assert.Equal(5, Resolve(User(Results(4)), ProjectFile(MinimalProject with { Search = new SearchSection(MaxResults: 5) })).Configuration.Search.MaxResults);
        Assert.Equal(6, Resolve(User(Results(4)), ProjectFile(MinimalProject with { Search = new SearchSection(MaxResults: 5) }), Local(Results(6))).Configuration.Search.MaxResults);

        var all = Resolve(User(Results(4)), ProjectFile(MinimalProject with { Search = new SearchSection(MaxResults: 5) }), Local(Results(6)), Call(Results(7)));
        Assert.Equal(7, all.Configuration.Search.MaxResults);
        Assert.Equal(ConfigLevel.Invocation, all.Configuration.Origins["search.maxResults"].Level);
    }

    [Fact]
    public void Same_named_connections_are_replaced_whole_not_deep_merged()
    {
        var user = User(new ConfigDocument
        {
            Connections = new Dictionary<string, ConnectionDefinition>
            {
                ["personal"] = new("https://kb.example.com", "AGENT_BRAIN_PERSONAL_KEY", ["general"], 5000, new ForwardSettings(Technologies: true)),
            },
        });
        var project = ProjectFile(MinimalProject with
        {
            Connections = new Dictionary<string, ConnectionDefinition> { ["personal"] = new("https://other.example.com", "OTHER_KEY") },
        });

        var connection = Assert.Single(Resolve(user, project).Configuration.Connections);

        Assert.Equal(new Uri("https://other.example.com"), connection.Url);
        Assert.Empty(connection.Collections);
        Assert.Equal(TimeSpan.FromMilliseconds(ConfigDefaults.ConnectionTimeoutMs), connection.Timeout);
        Assert.False(connection.Forward.Technologies);
    }

    [Fact]
    public void Enabled_false_removes_an_inherited_entry()
    {
        var user = User(new ConfigDocument
        {
            Connections = new Dictionary<string, ConnectionDefinition> { ["personal"] = new("https://kb.example.com", "KEY") },
            Collections = new Dictionary<string, CollectionRegistration> { ["shared.notes"] = new("notes") },
        });
        var project = ProjectFile(MinimalProject with
        {
            Connections = new Dictionary<string, ConnectionDefinition> { ["personal"] = new(Enabled: false) },
            Collections = new Dictionary<string, CollectionRegistration> { ["shared.notes"] = new(Enabled: false) },
        });

        var configuration = Resolve(user, project).Configuration;

        Assert.Empty(configuration.Connections);
        Assert.Empty(configuration.Collections);
    }

    [Fact]
    public void Inherit_user_false_ignores_the_user_file()
    {
        var user = User(new ConfigDocument
        {
            Search = new SearchSection(MaxResults: 3),
            Connections = new Dictionary<string, ConnectionDefinition> { ["personal"] = new("https://kb.example.com", "KEY") },
        });

        var configuration = Resolve(user, ProjectFile(MinimalProject with { InheritUser = false })).Configuration;

        Assert.Equal(ConfigDefaults.MaxResults, configuration.Search.MaxResults);
        Assert.Empty(configuration.Connections);
    }

    [Fact]
    public void Project_only_fields_in_the_user_file_are_ignored_with_a_warning()
    {
        var user = User(new ConfigDocument
        {
            Project = new ProjectSection("hijacked"),
            Writes = new WritesSection("elsewhere"),
            RuntimeDir = "/tmp/elsewhere",
            Proposals = "/tmp/proposals",
            InheritUser = false,
        });

        var resolution = Resolve(user, ProjectFile(MinimalProject));

        Assert.False(resolution.HasErrors, string.Join("; ", resolution.Diagnostics));
        Assert.Equal("space-game", resolution.Configuration.Project!.Id);
        Assert.Null(resolution.Configuration.DefaultWriteCollection);
        Assert.Equal(Path.Combine(ConfigDir, "local"), resolution.Configuration.RuntimeDirectory);
        Assert.Equal(5, resolution.Diagnostics.Count(d => d.Severity == IssueSeverity.Warning && d.Origin!.Level == ConfigLevel.User));
    }

    [Fact]
    public void Paths_resolve_against_the_declaring_file()
    {
        var user = User(new ConfigDocument { Collections = new Dictionary<string, CollectionRegistration> { ["personal.notes"] = new("notes") } });
        var project = ProjectFile(MinimalProject with
        {
            Collections = new Dictionary<string, CollectionRegistration> { ["space-game.memory"] = new("memory", ["read", "maintain"]) },
            RuntimeDir = "../.cache/brain",
        });
        var call = Call(new ConfigDocument { Collections = new Dictionary<string, CollectionRegistration> { ["extra"] = new("Packages/kb") } });

        var configuration = Resolve(user, project, call).Configuration;

        Assert.Equal(Path.Combine(UserDir, "notes"), configuration.Collections.Single(c => c.Id.Value == "personal.notes").Path);
        Assert.Equal(Path.Combine(ConfigDir, "memory"), configuration.Collections.Single(c => c.Id.Value == "space-game.memory").Path);
        Assert.Equal(Path.Combine(Root, "Packages", "kb"), configuration.Collections.Single(c => c.Id.Value == "extra").Path);
        Assert.Equal(Path.GetFullPath(Path.Combine(Root, ".cache", "brain")), configuration.RuntimeDirectory);
    }

    [Fact]
    public void Origins_are_recorded_for_every_effective_value()
    {
        var user = User(new ConfigDocument { Aliases = new Dictionary<string, IReadOnlyList<string>> { ["ecs"] = ["entity component system"] } });
        var project = ProjectFile(MinimalProject with
        {
            Collections = new Dictionary<string, CollectionRegistration> { ["space-game.memory"] = new("memory", ["read", "maintain"]) },
            Writes = new WritesSection("space-game.memory"),
        });

        var origins = Resolve(user, project).Configuration.Origins;

        Assert.Equal(ConfigLevel.User, origins["aliases.ecs"].Level);
        Assert.EndsWith("config.json", origins["aliases.ecs"].FilePath, StringComparison.Ordinal);
        Assert.Equal(ConfigLevel.Project, origins["collections.space-game.memory"].Level);
        Assert.Equal(ConfigLevel.Project, origins["writes.defaultCollection"].Level);
        Assert.Equal(ConfigLevel.Project, origins["project.id"].Level);
    }

    [Fact]
    public void Aliases_union_by_key_and_the_higher_level_wins_a_key()
    {
        var user = User(new ConfigDocument
        {
            Aliases = new Dictionary<string, IReadOnlyList<string>> { ["ecs"] = ["entity component system"], ["srp"] = ["scriptable render pipeline"] },
        });
        var project = ProjectFile(MinimalProject with { Aliases = new Dictionary<string, IReadOnlyList<string>> { ["ecs"] = ["entities", "dots"] } });

        var aliases = Resolve(user, project).Configuration.Aliases;

        Assert.Equal(2, aliases.Count);
        Assert.Equal(["ecs", "entities", "dots"], aliases.Single(a => a.Terms[0] == "ecs").Terms);
        Assert.Equal(["srp", "scriptable render pipeline"], aliases.Single(a => a.Terms[0] == "srp").Terms);
    }

    [Fact]
    public void Forwarding_is_off_by_default_and_a_project_can_switch_it_off_entirely()
    {
        var user = User(new ConfigDocument
        {
            Connections = new Dictionary<string, ConnectionDefinition>
            {
                ["personal"] = new("https://kb.example.com", "KEY", Forward: new ForwardSettings(Project: true, Files: true)),
                ["quiet"] = new("https://quiet.example.com", "KEY2"),
            },
        });

        var inherited = Resolve(user, ProjectFile(MinimalProject)).Configuration.Connections;
        Assert.Equal(new ForwardPolicy(true, false, true, false), inherited.Single(c => c.Name == "personal").Forward);
        Assert.Equal(ForwardPolicy.Nothing, inherited.Single(c => c.Name == "quiet").Forward);

        var restricted = Resolve(user, ProjectFile(MinimalProject with { ForwardContext = false })).Configuration.Connections;
        Assert.All(restricted, c => Assert.Equal(ForwardPolicy.Nothing, c.Forward));
    }

    [Fact]
    public void Default_write_collection_must_exist_and_be_maintainable()
    {
        var readOnly = ProjectFile(MinimalProject with
        {
            Collections = new Dictionary<string, CollectionRegistration> { ["unity.kb"] = new("kb", ["read", "propose"]) },
            Writes = new WritesSection("unity.kb"),
        });
        Assert.Contains(Resolve(readOnly).Diagnostics, d => d.Path == "writes.defaultCollection" && d.Severity == IssueSeverity.Error);

        var missing = ProjectFile(MinimalProject with { Writes = new WritesSection("nope") });
        Assert.Contains(Resolve(missing).Diagnostics, d => d.Path == "writes.defaultCollection");
    }

    [Fact]
    public void Collections_default_to_read_capability()
    {
        var project = ProjectFile(MinimalProject with { Collections = new Dictionary<string, CollectionRegistration> { ["unity.kb"] = new("kb") } });
        Assert.Equal(Capability.Read, Assert.Single(Resolve(project).Configuration.Collections).Capabilities);
    }

    public static TheoryData<string, ConfigDocument> InvalidDocuments => new()
    {
        { "search.maxResults", MinimalProject with { Search = new SearchSection(MaxResults: 0) } },
        { "search.relaxation", MinimalProject with { Search = new SearchSection(Relaxation: "loose") } },
        { "search.kinds", MinimalProject with { Search = new SearchSection(Kinds: ["rule"]) } },
        { "collections.Bad Name", MinimalProject with { Collections = new Dictionary<string, CollectionRegistration> { ["Bad Name"] = new("x") } } },
        { "collections.kb.path", MinimalProject with { Collections = new Dictionary<string, CollectionRegistration> { ["kb"] = new() } } },
        { "collections.kb.capabilities", MinimalProject with { Collections = new Dictionary<string, CollectionRegistration> { ["kb"] = new("x", ["admin"]) } } },
        { "connections.p.url", MinimalProject with { Connections = new Dictionary<string, ConnectionDefinition> { ["p"] = new("http://kb.example.com", "KEY") } } },
        { "connections.p.credentialEnv", MinimalProject with { Connections = new Dictionary<string, ConnectionDefinition> { ["p"] = new("https://kb.example.com", "not a var") } } },
        { "connections.p.timeoutMs", MinimalProject with { Connections = new Dictionary<string, ConnectionDefinition> { ["p"] = new("https://kb.example.com", "KEY", TimeoutMs: 5) } } },
        { "project.id", new ConfigDocument() },
        { "project.id", new ConfigDocument { Project = new ProjectSection("Space Game") } },
        { "schema", MinimalProject with { Schema = 2 } },
        { "aliases.ecs", MinimalProject with { Aliases = new Dictionary<string, IReadOnlyList<string>> { ["ecs"] = ["ECS"] } } },
    };

    [Theory]
    [MemberData(nameof(InvalidDocuments))]
    public void Invalid_values_are_reported_at_their_path(string path, ConfigDocument document)
    {
        var resolution = Resolve(ProjectFile(document));
        Assert.True(resolution.HasErrors);
        Assert.Contains(resolution.Diagnostics, d => d.Path == path && d.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void Plain_http_is_allowed_for_loopback_only()
    {
        var project = ProjectFile(MinimalProject with
        {
            Connections = new Dictionary<string, ConnectionDefinition> { ["local"] = new("http://127.0.0.1:5005", "KEY") },
        });
        Assert.False(Resolve(project).HasErrors);
    }

    [Fact]
    public void Without_a_project_only_user_level_sources_apply()
    {
        var user = User(new ConfigDocument { Connections = new Dictionary<string, ConnectionDefinition> { ["personal"] = new("https://kb.example.com", "KEY") } });

        var resolution = ConfigLayering.Resolve(new ConfigurationInput(null, [user]));

        Assert.False(resolution.HasErrors, string.Join("; ", resolution.Diagnostics));
        Assert.Null(resolution.Configuration.Project);
        Assert.Null(resolution.Configuration.RuntimeDirectory);
        Assert.Single(resolution.Configuration.Connections);
    }
}
