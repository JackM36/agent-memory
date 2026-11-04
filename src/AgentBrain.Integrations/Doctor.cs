using System.Text.Json.Nodes;
using AgentBrain.Composition;
using AgentBrain.Domain.Validation;

namespace AgentBrain.Integrations;

public enum CheckState
{
    Ok,
    Warning,
    Error,
}

public sealed record Check(string Name, CheckState State, string Detail);

/// <summary>
/// <c>brain doctor</c> (plan §9.4): configuration, collections, index health, interrupted batches, and
/// whether installed integrations are current and point at a command that resolves.
/// </summary>
public static class Doctor
{
    public static async Task<IReadOnlyList<Check>> RunAsync(RuntimeOptions options, string projectRoot, Func<string, bool> commandResolves, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(commandResolves);
        var checks = new List<Check>();

        var resolution = await BrainRuntime.ResolveConfigurationAsync(options, cancellationToken);
        if (!resolution.IsSuccess)
        {
            checks.Add(new Check("configuration", CheckState.Error, resolution.Error.Message));
            return checks;
        }

        var errors = resolution.Value.Diagnostics.Where(d => d.Severity == IssueSeverity.Error).ToList();
        checks.Add(errors.Count > 0
            ? new Check("configuration", CheckState.Error, string.Join("; ", errors))
            : new Check("configuration", resolution.Value.Configuration.Project is null ? CheckState.Warning : CheckState.Ok,
                resolution.Value.Configuration.Project is null ? "no project found; run brain init" : $"project {resolution.Value.Configuration.Project.Id}"));
        if (errors.Count > 0)
        {
            return checks;
        }

        var opened = await BrainRuntime.OpenAsync(options, cancellationToken);
        if (!opened.IsSuccess)
        {
            checks.Add(new Check("runtime", CheckState.Error, opened.Error.Message));
            return checks;
        }

        using var runtime = opened.Value;
        foreach (var warning in runtime.Diagnostics.Where(d => d.Severity == IssueSeverity.Warning && d.Path != "decisions"))
        {
            checks.Add(new Check("collections", CheckState.Warning, $"{warning.Path}: {warning.Message}"));
        }

        checks.Add(new Check("collections", CheckState.Ok, $"{runtime.Catalog.All.Count} local, {runtime.Configuration.Connections.Count} remote connection(s)"));
        var index = await runtime.Index.SynchronizeAsync(Application.Indexing.SyncMode.Incremental, cancellationToken);
        if (index.IsSuccess)
        {
            var problems = index.Value.Diagnostics.Count(d => d.Issue.Severity == IssueSeverity.Error);
            checks.Add(new Check("index", problems > 0 ? CheckState.Warning : CheckState.Ok,
                $"{index.Value.Status.Entries} entries, {problems} invalid file(s){(problems > 0 ? " — see brain index status" : "")}"));
        }
        else
        {
            checks.Add(new Check("index", CheckState.Warning, index.Error.Message));
        }

        var batches = await runtime.Publishing.StatusAsync(cancellationToken);
        checks.Add(batches.Count > 0
            ? new Check("batches", CheckState.Warning, $"{batches.Count} interrupted publication(s); run brain batch status")
            : new Check("batches", CheckState.Ok, "none interrupted"));

        checks.Add(DecisionCheck(runtime.Decisions.Status()));
        checks.AddRange(Integrations(projectRoot, commandResolves));
        return checks;
    }

    /// <summary>Heuristics need nothing. A model-backed port needs a key, a pinned model and at least one collection that allows it (ADR 0010).</summary>
    private static Check DecisionCheck(Application.Operations.Decisions.DecisionStatus status)
    {
        var used = status.Ports.Where(p => p.UsesModel).Select(p => $"{p.Port}={p.Mode}").ToList();
        if (used.Count == 0)
        {
            return new Check("decisions", CheckState.Ok, "all heuristic; no model is used");
        }

        return status.Problems.Count > 0
            ? new Check("decisions", CheckState.Warning, $"{string.Join(", ", used)}: {string.Join(" ", status.Problems)}")
            : new Check("decisions", CheckState.Ok, $"{string.Join(", ", used)}; {status.Model}, key set, may send {string.Join(", ", status.AllowedCollections)}");
    }

    private static IEnumerable<Check> Integrations(string root, Func<string, bool> commandResolves)
    {
        var agents = Path.Combine(root, "AGENTS.md");
        yield return File.Exists(agents) && File.ReadAllText(agents).Contains(Templates.SectionBegin, StringComparison.Ordinal)
            ? new Check("AGENTS.md", CheckState.Ok, "section current")
            : File.Exists(agents) && File.ReadAllText(agents).Contains("<!-- agent-brain:begin", StringComparison.Ordinal)
                ? new Check("AGENTS.md", CheckState.Warning, "section is from another version; run brain init")
                : new Check("AGENTS.md", CheckState.Warning, "no Agent Brain section; run brain init");

        foreach (var skill in new[] { Path.Combine(root, ".claude", "skills", "agent-brain", "SKILL.md"), Path.Combine(root, ".agents", "skills", "agent-brain", "SKILL.md") })
        {
            if (File.Exists(skill))
            {
                yield return File.ReadAllText(skill).Contains(Templates.VersionMarker, StringComparison.Ordinal)
                    ? new Check(Path.GetRelativePath(root, skill), CheckState.Ok, "current")
                    : new Check(Path.GetRelativePath(root, skill), CheckState.Warning, "older version; run brain init");
            }
        }

        foreach (var file in new[] { ".mcp.json", Path.Combine(".gemini", "settings.json") })
        {
            var path = Path.Combine(root, file);
            if (!File.Exists(path))
            {
                continue;
            }

            var command = (JsonNode.Parse(File.ReadAllText(path))?["mcpServers"]?[Templates.McpServerName]?["command"])?.GetValue<string>();
            yield return command is null
                ? new Check(file, CheckState.Warning, "no agent-brain MCP server registered; run brain init")
                : commandResolves(command)
                    ? new Check(file, CheckState.Ok, $"MCP server runs '{command}'")
                    : new Check(file, CheckState.Error, $"MCP command '{command}' does not resolve; put brain on PATH or re-run brain init");
        }

        var codex = Path.Combine(root, ".codex", "config.toml");
        if (File.Exists(codex))
        {
            yield return File.ReadAllText(codex).Contains($"[mcp_servers.{Templates.McpServerName}]", StringComparison.Ordinal)
                ? new Check(".codex/config.toml", CheckState.Ok, "MCP server registered")
                : new Check(".codex/config.toml", CheckState.Warning, "no agent-brain MCP server; run brain init --integrations codex");
        }
    }
}
