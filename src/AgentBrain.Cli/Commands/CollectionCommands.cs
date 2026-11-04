using System.CommandLine;
using System.Text.Json.Nodes;
using AgentBrain.Cli.Infrastructure;
using AgentBrain.Domain.Identity;
using AgentBrain.Infrastructure.Local.Configuration;
using AgentBrain.Integrations;

namespace AgentBrain.Cli.Commands;

/// <summary>
/// <c>brain collection create|add</c>: make a collection folder and register it, so a personal library or another
/// project's memory needs no hand-edited JSON. Registrations go to the user file by default (every project sees
/// them) or to this project's config with <c>--project</c>.
/// </summary>
internal static class CollectionCommands
{
    /// <summary>Who may change a registered collection, in the words the commands use.</summary>
    private static readonly Dictionary<string, string[]> Access = new(StringComparer.Ordinal)
    {
        ["read"] = ["read"],
        ["review"] = ["read", "propose", "publish"],
        ["maintain"] = ["read", "maintain"],
    };

    public static Command Create(CliIo io)
    {
        var project = new Option<bool>("--project") { Description = "Register in this project's config.json instead of the user file." };
        var knowledge = new Option<bool>("--knowledge") { Description = "Also make it the default destination for knowledge (writes.knowledge)." };
        return new Command("collection", "Create or register collections.") { CreateCommand(io, project, knowledge), AddCommand(io, project, knowledge) };
    }

    private static Command CreateCommand(CliIo io, Option<bool> project, Option<bool> knowledge)
    {
        var id = new Argument<string>("id") { Description = "Collection ID, e.g. library or library.unity." };
        var title = new Option<string?>("--title") { Description = "Title (default: from the ID)." };
        var path = new Option<string?>("--path") { Description = "Folder (default: ~/agent-brain/<id> for the user file, .agent-brain/<id> with --project)." };
        var technologies = DiscoveryCommands.Many("--tech", "Technology every entry applies to by default (e.g. unity). Repeatable.");
        var review = new Option<bool>("--review") { Description = "Saves become proposals you accept (default: saved directly)." };
        var command = new Command("create", "Create a collection folder for general knowledge and register it.") { id, title, path, technologies, review, project, knowledge };
        command.SetAction(parse =>
        {
            if (!CollectionId.TryParse(parse.GetValue(id), out var collection))
            {
                return Fail(io, $"'{parse.GetValue(id)}' is not a valid collection ID (lowercase words joined by '-' or '.').");
            }

            var target = Target(io, parse, project);
            if (target is null)
            {
                return ExitCodes.Usage;
            }

            var folder = parse.GetValue(path) is { } given
                ? Path.GetFullPath(given, io.WorkingDirectory)
                : target.Value.IsProject
                    ? Path.Combine(target.Value.ConfigDirectory, collection.Value)
                    : Path.Combine(HostEnvironment.Current.HomeDirectory, "agent-brain", collection.Value);

            var descriptor = Path.Combine(folder, CollectionDescriptorFile.FileName);
            if (File.Exists(descriptor))
            {
                var existing = CollectionDescriptorFile.ReadId(descriptor);
                if (existing != collection.Value)
                {
                    return Fail(io, $"{descriptor} already describes collection '{existing ?? "(unreadable)"}'.");
                }

                io.Out.WriteLine($"kept {descriptor}");
            }
            else
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(descriptor, CollectionDescriptorFile.Create(collection.Value, parse.GetValue(title) ?? CollectionDescriptorFile.TitleFrom(collection.Value), parse.GetValue(technologies) ?? []));
                io.Out.WriteLine($"created {descriptor}");
            }

            return Register(io, target.Value, collection.Value, folder, Access[parse.GetValue(review) ? "review" : "maintain"], parse.GetValue(knowledge));
        });
        return command;
    }

    private static Command AddCommand(CliIo io, Option<bool> project, Option<bool> knowledge)
    {
        var path = new Argument<string>("path") { Description = "A folder containing collection.json, e.g. another project's .agent-brain/memory." };
        var access = new Option<string>("--access") { Description = "read (default), review (proposals you accept) or maintain (direct saves).", DefaultValueFactory = _ => "read" };
        var command = new Command("add", "Register an existing collection, such as another project's memory for cross-project search.") { path, access, project, knowledge };
        command.SetAction(parse =>
        {
            if (!Access.TryGetValue(parse.GetValue(access)!, out var capabilities))
            {
                return Fail(io, "--access must be read, review or maintain.");
            }

            var folder = Path.GetFullPath(parse.GetValue(path)!, io.WorkingDirectory);
            var descriptor = Path.Combine(folder, CollectionDescriptorFile.FileName);
            if (CollectionDescriptorFile.ReadId(descriptor) is not { } id || !CollectionId.IsValid(id))
            {
                return Fail(io, $"{descriptor} is missing or has no valid 'id'.");
            }

            if (parse.GetValue(knowledge) && capabilities.Length == 1)
            {
                return Fail(io, "--knowledge needs --access review or maintain: nothing could be saved to a read-only target.");
            }

            var target = Target(io, parse, project);
            return target is null ? ExitCodes.Usage : Register(io, target.Value, id, folder, capabilities, parse.GetValue(knowledge));
        });
        return command;
    }

    private static int Register(CliIo io, (string File, string ConfigDirectory, bool IsProject) target, string id, string folder, string[] capabilities, bool knowledge)
    {
        // Project registrations stay relative when the folder is inside the project, so the config is portable.
        var root = Path.GetDirectoryName(target.ConfigDirectory)!;
        var registeredPath = target.IsProject && folder.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? Path.GetRelativePath(target.ConfigDirectory, folder)
            : folder;

        var patch = new JsonObject
        {
            ["schema"] = 1,
            ["collections"] = new JsonObject
            {
                [id] = new JsonObject
                {
                    ["path"] = registeredPath.Replace('\\', '/'),
                    ["capabilities"] = new JsonArray([.. capabilities.Select(c => (JsonNode)c)]),
                },
            },
        };
        if (knowledge)
        {
            patch["writes"] = new JsonObject { ["knowledge"] = id };
        }

        var change = FileEdits.MergeJson(target.File, patch);
        if (change.Action == FileAction.Skipped)
        {
            return Fail(io, $"{target.File}: {change.Detail}");
        }

        io.Out.WriteLine($"{change.Action.ToString().ToLowerInvariant()} {target.File}: collection '{id}' ({string.Join(", ", capabilities)}){(knowledge ? ", default for knowledge" : "")}");
        return ExitCodes.Success;
    }

    internal static (string File, string ConfigDirectory, bool IsProject)? Target(CliIo io, ParseResult parse, Option<bool> project)
    {
        if (!parse.GetValue(project))
        {
            var directory = ConfigLocations.UserDirectory(HostEnvironment.Current);
            Directory.CreateDirectory(directory);
            return (Path.Combine(directory, ConfigLocations.UserFileName), directory, false);
        }

        var location = ConfigLocations.FindProject(io.WorkingDirectory, parse.GetValue(GlobalOptions.Project), HostEnvironment.Current);
        if (location is null)
        {
            io.Error.WriteLine("error config_invalid: no project found; run brain init first or omit --project.");
            return null;
        }

        return (Path.Combine(location.ConfigDirectory, ConfigLocations.ProjectFileName), location.ConfigDirectory, true);
    }

    private static int Fail(CliIo io, string message)
    {
        io.Error.WriteLine("error validation_failed: " + message);
        return ExitCodes.Usage;
    }
}
