using System.CommandLine;
using System.Text.Json.Nodes;
using AgentBrain.Cli.Infrastructure;
using AgentBrain.Composition;
using AgentBrain.Domain.Identity;
using AgentBrain.Infrastructure.Local.Configuration;
using AgentBrain.Integrations;

namespace AgentBrain.Cli.Commands;

/// <summary><c>brain connection add|test</c>: register a remote host in the user (or project) configuration.</summary>
internal static class ConnectionCommands
{
    public static Command Create(CliIo io)
    {
        var name = new Argument<string>("name") { Description = "Connection name, e.g. personal." };
        var url = new Option<string>("--url") { Required = true, Description = "https URL of the brain-server." };
        var credentialEnv = new Option<string>("--credential-env") { Required = true, Description = "Environment variable that holds the API key (never the key itself)." };
        var collections = new Option<string?>("--collections") { Description = "Hosted collections to use, comma-separated (default: all the key can read)." };
        var forward = new Option<string?>("--forward") { Description = "Context to send: project, technologies, files, symbols (comma-separated). Default: none." };
        var timeout = new Option<int?>("--timeout-ms") { Description = "Per-request timeout." };
        var project = new Option<bool>("--project") { Description = "Write to this project's config.json instead of the user file." };
        var add = new Command("add", "Register a remote Agent Brain host (written to the user configuration by default, so every project sees it).")
        {
            name, url, credentialEnv, collections, forward, timeout, project,
        };

        add.SetAction(parse =>
        {
            var connectionName = parse.GetValue(name)!;
            if (!CollectionId.IsValid(connectionName))
            {
                io.Error.WriteLine("error validation_failed: connection names are lowercase words joined by '-' or '.'.");
                return ExitCodes.Usage;
            }

            var entry = new JsonObject { ["url"] = parse.GetValue(url), ["credentialEnv"] = parse.GetValue(credentialEnv) };
            if (parse.GetValue(collections) is { } list)
            {
                entry["collections"] = new JsonArray([.. list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(c => (JsonNode)c)]);
            }

            if (parse.GetValue(timeout) is { } ms)
            {
                entry["timeoutMs"] = ms;
            }

            var forwarded = (parse.GetValue(forward) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
            entry["forward"] = new JsonObject
            {
                ["project"] = forwarded.Contains("project"),
                ["technologies"] = forwarded.Contains("technologies"),
                ["files"] = forwarded.Contains("files"),
                ["symbols"] = forwarded.Contains("symbols"),
            };

            string path;
            if (parse.GetValue(project))
            {
                var location = ConfigLocations.FindProject(io.WorkingDirectory, parse.GetValue(GlobalOptions.Project), HostEnvironment.Current);
                if (location is null)
                {
                    io.Error.WriteLine("error config_invalid: no project found; run brain init first or omit --project.");
                    return ExitCodes.Usage;
                }

                path = Path.Combine(location.ConfigDirectory, ConfigLocations.ProjectFileName);
            }
            else
            {
                path = Path.Combine(ConfigLocations.UserDirectory(HostEnvironment.Current), ConfigLocations.UserFileName);
            }

            var change = FileEdits.MergeJson(path, new JsonObject { ["schema"] = 1, ["connections"] = new JsonObject { [connectionName] = entry } });
            if (change.Action == FileAction.Skipped)
            {
                io.Error.WriteLine($"error config_invalid: {path}: {change.Detail}");
                return ExitCodes.Usage;
            }

            io.Out.WriteLine($"{change.Action.ToString().ToLowerInvariant()} {path}: connection '{connectionName}' → {parse.GetValue(url)}");
            io.Out.WriteLine($"Set {parse.GetValue(credentialEnv)} to the API key, then run brain connection test {connectionName}.");
            return ExitCodes.Success;
        });

        var testName = new Argument<string>("name");
        var test = new Command("test", "Authenticate against a configured connection and list the collections its key can read.") { testName };
        test.SetAction(async (parse, ct) =>
        {
            var result = await ConnectionTester.TestAsync(CliContext.Options(parse, io), parse.GetValue(testName)!, ct);
            if (!result.IsSuccess)
            {
                return CliContext.Fail(io, CliContext.Renderer(parse, io), result.Error);
            }

            await io.Out.WriteLineAsync($"connection '{parse.GetValue(testName)}' ok; readable collections:");
            foreach (var line in result.Value)
            {
                await io.Out.WriteLineAsync("  " + line);
            }

            return ExitCodes.Success;
        });

        return new Command("connection", "Manage remote Agent Brain hosts.") { add, test };
    }
}
