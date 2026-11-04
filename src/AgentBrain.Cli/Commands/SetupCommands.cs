using System.CommandLine;
using System.Globalization;
using System.Text;
using AgentBrain.Cli.Infrastructure;
using AgentBrain.Domain.Identity;
using AgentBrain.Infrastructure.Local.Configuration;
using AgentBrain.Integrations;

namespace AgentBrain.Cli.Commands;

/// <summary><c>brain init</c>, <c>brain doctor</c> and <c>brain hook stop</c> (A10).</summary>
internal static class SetupCommands
{
    public static Command Init(CliIo io)
    {
        var projectId = new Option<string?>("--project-id") { Description = "Project ID (default: from the folder name)." };
        var integrations = new Option<string?>("--integrations")
        {
            Description = "claude, codex, gemini, opencode (comma-separated), all or none. Default: the agents already configured in this folder.",
        };
        var noHooks = new Option<bool>("--no-hooks") { Description = "Do not install the Stop hook that nudges agents to save knowledge." };
        var claudeMd = new Option<string>("--claude-md") { Description = "import (add @AGENTS.md to CLAUDE.md) or none.", DefaultValueFactory = _ => "import" };
        var technologies = DiscoveryCommands.Many("--tech", "A technology this project uses (e.g. unity, react). Repeatable.");
        var command = new Command("init", "Set up Agent Brain in this project: configuration, project memory, AGENTS.md and agent integrations.")
        {
            projectId, integrations, noHooks, claudeMd, technologies,
        };

        command.SetAction(parse =>
        {
            var root = Path.GetFullPath(parse.GetValue(GlobalOptions.Project) ?? io.WorkingDirectory);
            var id = parse.GetValue(projectId) ?? Slug.TryCreate(Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), 64) ?? "project";
            if (!Names.IsProjectId(id))
            {
                io.Error.WriteLine($"error validation_failed: '{id}' is not a valid project ID (lowercase words joined by '-' or '.').");
                return ExitCodes.Usage;
            }

            var hosts = ParseHosts(parse.GetValue(integrations), root);
            if (hosts is null)
            {
                io.Error.WriteLine("error validation_failed: --integrations takes claude, codex, gemini, opencode, all or none.");
                return ExitCodes.Usage;
            }

            var options = new InitOptions(root, id)
            {
                Hosts = hosts,
                Hooks = !parse.GetValue(noHooks),
                ClaudeMd = parse.GetValue(claudeMd) == "none" ? ClaudeMdMode.None : ClaudeMdMode.Import,
                Technologies = [.. (parse.GetValue(technologies) ?? []).Select(t => t.Trim().ToLowerInvariant())],
                Command = IntegrationInstaller.ResolveCommand(Environment.ProcessPath, Environment.GetEnvironmentVariable, OperatingSystem.IsWindows()),
            };

            var text = new StringBuilder().Append(CultureInfo.InvariantCulture, $"Agent Brain set up for project '{id}'");
            text.Append(hosts.Count > 0 ? $" with {string.Join(", ", hosts.Select(h => h.ToString().ToLowerInvariant()))}\n" : " (no agent integrations; pass --integrations)\n");
            foreach (var change in IntegrationInstaller.Init(options))
            {
                text.Append(CultureInfo.InvariantCulture, $"  {change.Action.ToString().ToLowerInvariant(),-9} {Path.GetRelativePath(root, change.Path).Replace('\\', '/')}");
                text.Append(change.Detail is null ? "\n" : $" ({change.Detail})\n");
            }

            text.Append("Next: brain doctor, then brain search \"<what you are working on>\".\n");
            io.Out.Write(text.ToString());
            return ExitCodes.Success;
        });
        return command;
    }

    public static Command Doctor(CliIo io)
    {
        var command = new Command("doctor", "Check configuration, collections, the index, interrupted batches and agent integrations.");
        command.SetAction(async (parse, ct) =>
        {
            var options = CliContext.Options(parse, io);
            var root = ConfigLocations.FindProject(io.WorkingDirectory, options.ProjectDirectory, HostEnvironment.Current)?.RootDirectory ?? io.WorkingDirectory;
            var checks = await Integrations.Doctor.RunAsync(options, root, CommandResolves, ct);
            foreach (var check in checks)
            {
                await io.Out.WriteLineAsync($"{check.State.ToString().ToLowerInvariant(),-7} {check.Name}: {check.Detail}");
            }

            return checks.Any(c => c.State == CheckState.Error) ? ExitCodes.Usage : ExitCodes.Success;
        });
        return command;
    }

    public static Command Hook(CliIo io)
    {
        var host = new Option<string>("--host") { Description = "claude or codex.", Required = true };
        var stop = new Command("stop", "Stop-hook entry point: reads the hook JSON on stdin and nudges once when project files changed but nothing was saved.") { host };
        stop.SetAction(async (parse, ct) =>
        {
            var input = await io.In.ReadToEndAsync(ct);
            var decision = StopHook.Evaluate(input, path => File.Exists(path) ? File.ReadAllText(path) : null);
            await io.Out.WriteAsync(StopHook.Output(decision));
            return ExitCodes.Success;
        });
        return new Command("hook", "Entry points for agent hooks.") { stop };
    }

    private static HashSet<AgentHost>? ParseHosts(string? text, string root)
    {
        if (text is null)
        {
            var detected = new HashSet<AgentHost>();
            if (Directory.Exists(Path.Combine(root, ".claude")) || File.Exists(Path.Combine(root, "CLAUDE.md")))
            {
                detected.Add(AgentHost.Claude);
            }

            if (Directory.Exists(Path.Combine(root, ".codex")) || Directory.Exists(Path.Combine(root, ".agents")))
            {
                detected.Add(AgentHost.Codex);
            }

            if (Directory.Exists(Path.Combine(root, ".gemini")))
            {
                detected.Add(AgentHost.Gemini);
            }

            if (File.Exists(Path.Combine(root, "opencode.json")) || Directory.Exists(Path.Combine(root, ".opencode")))
            {
                detected.Add(AgentHost.OpenCode);
            }

            return detected;
        }

        if (text == "none")
        {
            return [];
        }

        if (text == "all")
        {
            return [.. Enum.GetValues<AgentHost>()];
        }

        var hosts = new HashSet<AgentHost>();
        foreach (var name in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Enum.TryParse<AgentHost>(name, ignoreCase: true, out var host))
            {
                return null;
            }

            hosts.Add(host);
        }

        return hosts;
    }

    private static bool CommandResolves(string command)
    {
        if (Path.IsPathRooted(command))
        {
            return File.Exists(command);
        }

        var names = OperatingSystem.IsWindows() ? new[] { command + ".exe", command + ".cmd", command } : [command];
        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => names.Any(n => File.Exists(Path.Combine(dir, n))));
    }
}
