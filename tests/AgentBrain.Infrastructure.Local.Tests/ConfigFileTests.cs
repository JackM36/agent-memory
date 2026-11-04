using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AgentBrain.Application.Common;
using AgentBrain.Application.Configuration;
using AgentBrain.Infrastructure.Local.Configuration;

namespace AgentBrain.Infrastructure.Local.Tests;

public sealed class ConfigFileTests
{
    [Fact]
    public void Json_with_comments_and_trailing_commas_parses()
    {
        const string json = """
            {
              "$schema": "https://agent-brain.dev/schema/config.v1.schema.json",
              // the project
              "schema": 1,
              "project": { "id": "space-game", "technologies": ["unity", "csharp"], "packages": { "unity": "2022.3.10f1" } },
              "collections": {
                "space-game.memory": { "path": "memory", "capabilities": ["read", "maintain"], "immediatePublish": false },
              },
              "connections": {
                "personal": { "url": "https://kb.example.com", "credentialEnv": "AGENT_BRAIN_PERSONAL_KEY", "forward": { "technologies": true } }
              },
              "writes": { "defaultCollection": "space-game.memory" },
              "search": { "relaxation": "broad", "maxResults": 8, "kinds": ["memory"] },
              "aliases": { "ecs": ["entity component system"] },
            }
            """;

        var result = ConfigFileReader.Parse(json, "config.json");

        Assert.True(result.IsSuccess, result.Error?.Message);
        var d = result.Value;
        Assert.Equal("space-game", d.Project!.Id);
        Assert.Equal("2022.3.10f1", d.Project.Packages!["unity"]);
        Assert.Equal(["read", "maintain"], d.Collections!["space-game.memory"].Capabilities);
        Assert.True(d.Connections!["personal"].Forward!.Technologies);
        Assert.Equal(8, d.Search!.MaxResults);
        Assert.Equal(["entity component system"], d.Aliases!["ecs"]);
    }

    [Theory]
    [InlineData("""{ "serach": { } }""")]
    [InlineData("""{ "search": { "maxResult": 3 } }""")]
    [InlineData("""{ "search": { "maxResults": "10" } }""")]
    [InlineData("""{ "schema": 1, """)]
    [InlineData("null")]
    public void Typos_and_malformed_json_are_config_errors(string json)
    {
        var result = ConfigFileReader.Parse(json, "/repo/.agent-brain/config.json");

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCode.ConfigInvalid, result.Error.Code);
        Assert.Contains("/repo/.agent-brain/config.json", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Schema_file_and_file_model_list_the_same_properties()
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot, "docs", "schema", "config.v1.schema.json")));
        var context = ConfigJsonContext.Default;

        AssertSameProperties(schema.RootElement, context.ConfigFile, "");
    }

    private static void AssertSameProperties(JsonElement schemaObject, JsonTypeInfo typeInfo, string path)
    {
        var schemaProperties = schemaObject.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToList();
        var modelProperties = typeInfo.Properties.Select(p => p.Name).Order(StringComparer.Ordinal).ToList();
        Assert.True(schemaProperties.SequenceEqual(modelProperties),
            $"Schema and model differ at '{path}': schema [{string.Join(", ", schemaProperties)}], model [{string.Join(", ", modelProperties)}].");

        foreach (var property in typeInfo.Properties)
        {
            var nested = typeInfo.Options.GetTypeInfo(property.PropertyType);
            var schemaProperty = schemaObject.GetProperty("properties").GetProperty(property.Name);
            if (nested.Kind == JsonTypeInfoKind.Object && schemaProperty.TryGetProperty("properties", out _))
            {
                AssertSameProperties(schemaProperty, nested, $"{path}.{property.Name}");
            }
            else if (nested.Kind == JsonTypeInfoKind.Dictionary
                && schemaProperty.TryGetProperty("additionalProperties", out var items)
                && items.ValueKind == JsonValueKind.Object
                && items.TryGetProperty("properties", out _))
            {
                AssertSameProperties(items, typeInfo.Options.GetTypeInfo(nested.ElementType!), $"{path}.{property.Name}.*");
            }
        }
    }
}

public sealed class ConfigDiscoveryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("brain-discovery-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string CreateProject(string name, string json = """{ "schema": 1, "project": { "id": "space-game" } }""")
    {
        var configDir = Directory.CreateDirectory(Path.Combine(_root, name, ConfigLocations.ProjectDirectoryName)).FullName;
        File.WriteAllText(Path.Combine(configDir, ConfigLocations.ProjectFileName), json);
        return Path.Combine(_root, name);
    }

    private HostEnvironment Environment(Dictionary<string, string>? variables = null, bool windows = false) =>
        new(name => variables?.GetValueOrDefault(name), windows, Path.Combine(_root, "home"));

    [Fact]
    public void Discovery_walks_up_from_the_working_directory()
    {
        var project = CreateProject("game");
        var nested = Directory.CreateDirectory(Path.Combine(project, "Assets", "Scripts")).FullName;

        var location = ConfigLocations.FindProject(nested, null, Environment());

        Assert.Equal(project, location!.RootDirectory);
        Assert.Equal(Path.Combine(project, ".agent-brain"), location.ConfigDirectory);
    }

    [Fact]
    public void Explicit_directory_then_environment_variable_win_over_walking_up()
    {
        var walked = CreateProject("walked");
        var fromEnv = CreateProject("from-env");
        var explicitProject = CreateProject("explicit");
        var env = Environment(new() { [ConfigLocations.ProjectVariable] = fromEnv });

        Assert.Equal(explicitProject, ConfigLocations.FindProject(walked, explicitProject, env)!.RootDirectory);
        Assert.Equal(fromEnv, ConfigLocations.FindProject(walked, null, env)!.RootDirectory);
        Assert.Null(ConfigLocations.FindProject(walked, Path.Combine(_root, "missing"), env));
    }

    [Theory]
    [InlineData(true, "APPDATA", "appdata")]
    [InlineData(false, "XDG_CONFIG_HOME", "xdg")]
    public void User_directory_follows_platform_conventions(bool windows, string variable, string folder)
    {
        var configured = Environment(new() { [variable] = Path.Combine(_root, folder) }, windows);
        Assert.Equal(Path.Combine(_root, folder, "agent-brain"), ConfigLocations.UserDirectory(configured));

        var fallback = Environment(windows: windows);
        var expected = windows ? Path.Combine(_root, "home", "AppData", "Roaming", "agent-brain") : Path.Combine(_root, "home", ".config", "agent-brain");
        Assert.Equal(expected, ConfigLocations.UserDirectory(fallback));

        var overridden = Environment(new() { [ConfigLocations.ConfigDirVariable] = Path.Combine(_root, "custom") }, windows);
        Assert.Equal(Path.Combine(_root, "custom"), ConfigLocations.UserDirectory(overridden));
    }

    [Fact]
    public async Task Source_loads_user_project_local_and_invocation_documents()
    {
        var project = CreateProject("game", """{ "schema": 1, "project": { "id": "space-game" }, "search": { "maxResults": 5 } }""");
        File.WriteAllText(Path.Combine(project, ".agent-brain", ConfigLocations.LocalFileName), """{ "search": { "maxResults": 6 } }""");
        var userDir = Directory.CreateDirectory(Path.Combine(_root, "user")).FullName;
        File.WriteAllText(Path.Combine(userDir, "config.json"), """{ "search": { "maxResults": 4 } }""");
        var source = new ConfigFileSource(Environment(new() { [ConfigLocations.ConfigDirVariable] = userDir }));

        var input = await source.LoadAsync(
            new ConfigurationRequest(project, Invocation: new ConfigDocument { Search = new SearchSection(MaxResults: 7) }),
            TestContext.Current.CancellationToken);

        Assert.True(input.IsSuccess, input.Error?.Message);
        Assert.Equal([ConfigLevel.User, ConfigLevel.Project, ConfigLevel.ProjectLocal, ConfigLevel.Invocation], input.Value.Documents.Select(d => d.Level));
        var resolution = ConfigLayering.Resolve(input.Value);
        Assert.False(resolution.HasErrors, string.Join("; ", resolution.Diagnostics));
        Assert.Equal(7, resolution.Configuration.Search.MaxResults);
    }

    [Fact]
    public async Task A_broken_file_fails_loading_with_its_path()
    {
        var project = CreateProject("broken", """{ "schema": 1, "projet": {} }""");
        var source = new ConfigFileSource(Environment(new() { [ConfigLocations.ConfigDirVariable] = Path.Combine(_root, "nouser") }));

        var input = await source.LoadAsync(new ConfigurationRequest(project), TestContext.Current.CancellationToken);

        Assert.False(input.IsSuccess);
        Assert.Contains("config.json", input.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_project_only_the_user_file_loads()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_root, "elsewhere")).FullName;
        var source = new ConfigFileSource(Environment(new() { [ConfigLocations.ConfigDirVariable] = Path.Combine(_root, "nouser") }));

        var input = await source.LoadAsync(new ConfigurationRequest(outside), TestContext.Current.CancellationToken);

        Assert.True(input.IsSuccess);
        Assert.Null(input.Value.Project);
        Assert.Empty(input.Value.Documents);
    }
}

internal static class TestPaths
{
    public static string RepositoryRoot { get; } = Find();

    private static string Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AgentBrain.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root (AgentBrain.slnx) not found.");
    }
}
