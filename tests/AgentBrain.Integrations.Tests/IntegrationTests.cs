using System.Text.Json;

namespace AgentBrain.Integrations.Tests;

public sealed class IntegrationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("brain-init-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private InitOptions Options(params AgentHost[] hosts) => new(_root, "space-game") { Hosts = hosts.ToHashSet(), Technologies = ["unity"] };

    private string Read(string relative) => File.ReadAllText(Path.Combine(_root, relative));

    [Fact]
    public void Init_creates_configuration_memory_and_agent_files()
    {
        var changes = IntegrationInstaller.Init(Options(AgentHost.Claude, AgentHost.Codex, AgentHost.Gemini, AgentHost.OpenCode));

        Assert.All(changes, c => Assert.Equal(FileAction.Created, c.Action));
        Assert.Contains("\"space-game.memory\"", Read(".agent-brain/config.json"), StringComparison.Ordinal);
        Assert.Contains("\"projects\": [\"space-game\"]", Read(".agent-brain/memory/collection.json"), StringComparison.Ordinal);
        Assert.Contains(".agent-brain/local/", Read(".gitignore"), StringComparison.Ordinal);
        Assert.Contains(Templates.SectionBegin, Read("AGENTS.md"), StringComparison.Ordinal);
        Assert.Contains("@AGENTS.md", Read("CLAUDE.md"), StringComparison.Ordinal);
        Assert.Contains("[mcp_servers.agent-brain]", Read(".codex/config.toml"), StringComparison.Ordinal);
        Assert.Contains(Templates.VersionMarker, Read(".agents/skills/agent-brain/SKILL.md"), StringComparison.Ordinal);
        Assert.Contains("brain hook stop --host claude", Read(".claude/settings.json"), StringComparison.Ordinal);
        Assert.Contains("brain hook stop --host codex", Read(".codex/hooks.json"), StringComparison.Ordinal);
        using var mcp = JsonDocument.Parse(Read(".mcp.json"));
        Assert.Equal("brain", mcp.RootElement.GetProperty("mcpServers").GetProperty("agent-brain").GetProperty("command").GetString());
        using var opencode = JsonDocument.Parse(Read("opencode.json"));
        Assert.Equal("local", opencode.RootElement.GetProperty("mcp").GetProperty("agent-brain").GetProperty("type").GetString());
    }

    [Fact]
    public void Init_is_idempotent()
    {
        IntegrationInstaller.Init(Options(AgentHost.Claude, AgentHost.Codex));
        var again = IntegrationInstaller.Init(Options(AgentHost.Claude, AgentHost.Codex));

        Assert.All(again, c => Assert.Equal(FileAction.Unchanged, c.Action));
        Assert.Single(Read("AGENTS.md").Split(Templates.SectionBegin)[1..]);
    }

    [Fact]
    public void Existing_files_are_merged_preserved_and_backed_up()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".claude"));
        File.WriteAllText(Path.Combine(_root, ".claude", "settings.json"),
            """{ "permissions": { "allow": ["Bash(ls)"] }, "hooks": { "Stop": [ { "hooks": [ { "type": "command", "command": "other-hook" } ] } ] } }""");
        File.WriteAllText(Path.Combine(_root, "AGENTS.md"), "# Project rules\n\nAlways run tests.\n");
        Directory.CreateDirectory(Path.Combine(_root, ".codex"));
        File.WriteAllText(Path.Combine(_root, ".codex", "config.toml"), "model = \"o4\"\n\n[mcp_servers.other]\ncommand = \"x\"\n");

        IntegrationInstaller.Init(Options(AgentHost.Claude, AgentHost.Codex));

        var settings = Read(".claude/settings.json");
        Assert.Contains("Bash(ls)", settings, StringComparison.Ordinal);
        Assert.Contains("other-hook", settings, StringComparison.Ordinal);
        Assert.Contains("brain hook stop --host claude", settings, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_root, ".claude", "settings.json" + FileEdits.BackupSuffix)));
        Assert.StartsWith("# Project rules\n\nAlways run tests.\n", Read("AGENTS.md"), StringComparison.Ordinal);
        Assert.StartsWith("model = \"o4\"\n\n[mcp_servers.other]", Read(".codex/config.toml"), StringComparison.Ordinal);
        Assert.Contains("[mcp_servers.agent-brain]", Read(".codex/config.toml"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_newer_section_replaces_the_old_one_in_place()
    {
        File.WriteAllText(Path.Combine(_root, "AGENTS.md"), "Intro\n<!-- agent-brain:begin v0 -->\nold text\n<!-- agent-brain:end -->\nOutro\n");

        IntegrationInstaller.Init(Options());

        var text = Read("AGENTS.md");
        Assert.StartsWith("Intro\n" + Templates.SectionBegin, text, StringComparison.Ordinal);
        Assert.EndsWith(Templates.SectionEnd + "\nOutro\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("old text", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_existing_json_is_skipped_not_overwritten()
    {
        File.WriteAllText(Path.Combine(_root, ".mcp.json"), "{ not json");
        var change = IntegrationInstaller.Init(Options(AgentHost.Claude)).Single(c => c.Path.EndsWith(".mcp.json", StringComparison.Ordinal));
        Assert.Equal(FileAction.Skipped, change.Action);
        Assert.Equal("{ not json", Read(".mcp.json"));
    }

    private const string ClaudeEdit = """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Edit","input":{"file_path":"C:\\repo\\src\\Save.cs","old_string":"a"}}]}}""";
    private const string ClaudeMemoryEdit = """{"type":"tool_use","name":"Write","input":{"file_path":"/repo/.agent-brain/memory/memory/x.md"}}""";
    private const string CodexPatch = """{"type":"function_call","arguments":"*** Begin Patch\n*** Update File: src/app.ts\n@@"}""";
    private const string McpSave = """{"type":"tool_use","name":"mcp__agent-brain__save","input":{"action":"create"}}""";

    [Theory]
    [InlineData(ClaudeEdit, true)]
    [InlineData(CodexPatch, true)]
    [InlineData(ClaudeMemoryEdit, false)]
    [InlineData(ClaudeEdit + "\n" + McpSave, false)]
    [InlineData(ClaudeEdit + "\n{\"command\":\"brain new memory --title x\"}", false)]
    [InlineData("""{"type":"tool_use","name":"Read","input":{"file_path":"/repo/src/a.cs"}}""", false)]
    public void Stop_hook_nudges_only_when_project_files_changed_without_a_save(string transcript, bool nudge)
    {
        var decision = StopHook.Evaluate("""{"transcript_path":"t.jsonl","stop_hook_active":false}""", _ => transcript);

        Assert.Equal(nudge, decision.BlockReason is not null);
        if (nudge)
        {
            using var output = JsonDocument.Parse(StopHook.Output(decision));
            Assert.Equal("block", output.RootElement.GetProperty("decision").GetString());
        }
        else
        {
            Assert.Equal("", StopHook.Output(decision));
        }
    }

    [Fact]
    public void Stop_hook_never_blocks_twice_or_on_bad_input()
    {
        Assert.Null(StopHook.Evaluate("""{"transcript_path":"t","stop_hook_active":true}""", _ => ClaudeEdit).BlockReason);
        Assert.Null(StopHook.Evaluate("not json", _ => ClaudeEdit).BlockReason);
        Assert.Null(StopHook.Evaluate("""{"transcript_path":"missing"}""", _ => null).BlockReason);
    }

    [Fact]
    public void Command_resolution_prefers_path_then_the_running_binary()
    {
        var bin = Directory.CreateDirectory(Path.Combine(_root, "bin")).FullName;
        Assert.Equal("/opt/brain", IntegrationInstaller.ResolveCommand("/opt/brain", _ => bin, isWindows: false));
        File.WriteAllText(Path.Combine(bin, "brain"), "");
        Assert.Equal("brain", IntegrationInstaller.ResolveCommand("/opt/brain", _ => bin, isWindows: false));
    }
}
