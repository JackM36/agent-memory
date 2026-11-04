using System.Text.Json.Nodes;
using AgentBrain.Domain.Identity;

namespace AgentBrain.Integrations;

/// <summary>Agent products <c>brain init</c> can wire up.</summary>
public enum AgentHost
{
    Claude,
    Codex,
    Gemini,
    OpenCode,
}

public enum ClaudeMdMode
{
    /// <summary>Create CLAUDE.md with <c>@AGENTS.md</c>, or add that import to an existing one.</summary>
    Import,

    /// <summary>Leave CLAUDE.md alone.</summary>
    None,
}

public sealed record InitOptions(string ProjectRoot, string ProjectId)
{
    public IReadOnlyList<string> Technologies { get; init; } = [];

    public IReadOnlySet<AgentHost> Hosts { get; init; } = new HashSet<AgentHost>();

    public bool Hooks { get; init; } = true;

    public ClaudeMdMode ClaudeMd { get; init; } = ClaudeMdMode.Import;

    /// <summary>The command agents run: <c>brain</c> when it is on PATH, otherwise an absolute path.</summary>
    public string Command { get; init; } = "brain";
}

/// <summary>
/// <c>brain init</c> (plan §9.4, A10): project configuration, the project-memory collection, Git ignore
/// entries, the AGENTS.md section and per-agent integrations. Safe to re-run: existing files are merged,
/// never overwritten, and backed up before the first change. Integration file formats follow each product's
/// documentation at the time of writing; <c>brain doctor</c> reports what it finds.
/// </summary>
public static class IntegrationInstaller
{
    public static IReadOnlyList<FileChange> Init(InitOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Names.IsProjectId(options.ProjectId))
        {
            throw new ArgumentException($"'{options.ProjectId}' is not a valid project ID (lowercase words joined by '-' or '.').", nameof(options));
        }

        var root = Path.GetFullPath(options.ProjectRoot);
        var brain = Path.Combine(root, ".agent-brain");
        var changes = new List<FileChange>
        {
            FileEdits.WriteIfMissing(Path.Combine(brain, "config.json"), Templates.ProjectConfig(options.ProjectId, options.Technologies)),
            FileEdits.WriteIfMissing(Path.Combine(brain, "memory", "collection.json"), Templates.MemoryCollection(options.ProjectId)),
            FileEdits.EnsureLines(Path.Combine(root, ".gitignore"), [".agent-brain/config.local.json", ".agent-brain/local/"]),
            FileEdits.UpsertSection(Path.Combine(root, "AGENTS.md"), Templates.AgentsSection, Templates.SectionBegin, Templates.SectionEnd),
        };
        Directory.CreateDirectory(Path.Combine(brain, "proposals"));

        if (options.Hosts.Contains(AgentHost.Claude))
        {
            changes.AddRange(Claude(root, options));
        }

        if (options.Hosts.Contains(AgentHost.Codex))
        {
            changes.AddRange(Codex(root, options));
        }

        if (options.Hosts.Contains(AgentHost.Gemini))
        {
            changes.Add(FileEdits.MergeJson(Path.Combine(root, ".gemini", "settings.json"), McpServers(options.Command)));
        }

        if (options.Hosts.Contains(AgentHost.OpenCode))
        {
            changes.Add(FileEdits.MergeJson(Path.Combine(root, "opencode.json"), new JsonObject
            {
                ["mcp"] = new JsonObject
                {
                    [Templates.McpServerName] = new JsonObject
                    {
                        ["type"] = "local",
                        ["command"] = new JsonArray(options.Command, "mcp"),
                        ["enabled"] = true,
                    },
                },
            }));
        }

        return changes;
    }

    private static IEnumerable<FileChange> Claude(string root, InitOptions options)
    {
        yield return FileEdits.MergeJson(Path.Combine(root, ".mcp.json"), McpServers(options.Command));
        yield return FileEdits.WriteOwned(Path.Combine(root, ".claude", "skills", "agent-brain", "SKILL.md"), Templates.Skill);
        if (options.Hooks)
        {
            yield return FileEdits.MergeJson(Path.Combine(root, ".claude", "settings.json"), StopHook($"{options.Command} hook stop --host claude", timeout: null));
        }

        if (options.ClaudeMd == ClaudeMdMode.Import)
        {
            var claudeMd = Path.Combine(root, "CLAUDE.md");
            yield return File.Exists(claudeMd) && File.ReadAllText(claudeMd).Contains("@AGENTS.md", StringComparison.Ordinal)
                ? new FileChange(claudeMd, FileAction.Unchanged)
                : FileEdits.EnsureLines(claudeMd, ["@AGENTS.md"]);
        }
    }

    private static IEnumerable<FileChange> Codex(string root, InitOptions options)
    {
        yield return FileEdits.EnsureTomlTable(
            Path.Combine(root, ".codex", "config.toml"),
            $"mcp_servers.{Templates.McpServerName}",
            $"[mcp_servers.{Templates.McpServerName}]\ncommand = \"{options.Command.Replace("\\", "\\\\", StringComparison.Ordinal)}\"\nargs = [\"mcp\"]\n");
        yield return FileEdits.WriteOwned(Path.Combine(root, ".agents", "skills", "agent-brain", "SKILL.md"), Templates.Skill);
        if (options.Hooks)
        {
            yield return FileEdits.MergeJson(Path.Combine(root, ".codex", "hooks.json"), StopHook($"{options.Command} hook stop --host codex", timeout: 30));
        }
    }

    private static JsonObject McpServers(string command) => new()
    {
        ["mcpServers"] = new JsonObject
        {
            [Templates.McpServerName] = new JsonObject { ["command"] = command, ["args"] = new JsonArray("mcp") },
        },
    };

    private static JsonObject StopHook(string command, int? timeout)
    {
        var hook = new JsonObject { ["type"] = "command", ["command"] = command };
        if (timeout is { } seconds)
        {
            hook["timeout"] = seconds;
        }

        return new JsonObject
        {
            ["hooks"] = new JsonObject
            {
                ["Stop"] = new JsonArray(new JsonObject { ["hooks"] = new JsonArray(hook) }),
            },
        };
    }

    /// <summary>The command to register: <c>brain</c> when that resolves on PATH to this binary's name, otherwise this binary's absolute path.</summary>
    public static string ResolveCommand(string? processPath, Func<string, string?> getVariable, bool isWindows)
    {
        ArgumentNullException.ThrowIfNull(getVariable);
        var name = isWindows ? "brain.exe" : "brain";
        var path = getVariable("PATH") ?? "";
        var onPath = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Any(dir => File.Exists(Path.Combine(dir, name)));
        // Under "dotnet brain.dll" the process is the dotnet host, which is not a usable MCP command.
        var isDotnetHost = processPath is not null && Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        return onPath || processPath is null || isDotnetHost ? "brain" : processPath;
    }
}
