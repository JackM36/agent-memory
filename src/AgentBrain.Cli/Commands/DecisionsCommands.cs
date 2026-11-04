using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBrain.Cli.Infrastructure;
using AgentBrain.Domain.Entries;
using AgentBrain.Domain.Identity;
using AgentBrain.Integrations;

namespace AgentBrain.Cli.Commands;

/// <summary>
/// <c>brain decisions status|classify|gate|allow|deny</c> (ADR 0010): which implementation each decision port uses,
/// the model-backed classifier and capture gate on their own, and the per-collection privacy switch.
/// </summary>
internal static class DecisionsCommands
{
    public static Command Create(CliIo io)
    {
        var project = new Option<bool>("--project") { Description = "Edit this project's config.json instead of the user file." };
        return new Command("decisions", "Heuristic or model-backed decisions: status, classify, gate, allow, deny.")
        {
            Status(io), Classify(io), Gate(io), Switch(io, project, "allow", true), Switch(io, project, "deny", false),
        };
    }

    private static Command Status(CliIo io)
    {
        var command = new Command("status", "Show which implementation each decision port uses, and what would stop a model from being used.");
        command.SetAction((parse, ct) => CliContext.RunAsync(parse, io, (runtime, renderer) =>
        {
            var s = runtime.Decisions.Status();
            var text = new StringBuilder();
            foreach (var port in s.Ports)
            {
                text.Append(CultureInfo.InvariantCulture, $"{port.Port,-12} {port.Mode}\n");
            }

            text.Append(CultureInfo.InvariantCulture, $"model        {s.Model}{(s.ModelIsAlias ? " (alias)" : "")}\n");
            text.Append(CultureInfo.InvariantCulture, $"key          ${s.KeyVariable} is {(s.KeySet ? "set" : "not set")}\n");
            text.Append(CultureInfo.InvariantCulture, $"may send     {(s.AllowedCollections.Count == 0 ? "(no collection)" : string.Join(", ", s.AllowedCollections))}\n");
            foreach (var problem in s.Problems)
            {
                text.Append("warning ").Append(problem).Append('\n');
            }

            return Task.FromResult(CliContext.Write(io, text.ToString(), renderer));
        }, ct));
        return command;
    }

    private static Command Classify(CliIo io)
    {
        var kind = new Argument<string>("kind") { Description = $"The kind you intend to save: {WireNames.Kinds.Joined}." };
        var title = new Option<string>("--title") { Required = true, Description = "Title of the entry." };
        var summary = new Option<string>("--summary") { Required = true, Description = "Its summary." };
        var tags = DiscoveryCommands.Many("--tag", "Its tags.");
        var body = new Option<string?>("--body") { Description = "The beginning of its body (at most 600 characters are used)." };
        var collection = new Option<string?>("--collection") { Description = "Where it would be saved (default: writes.memory)." };
        var command = new Command("classify", "Ask the classifier what an entry is. It only advises; your kind and destination win.") { kind, title, summary, tags, body, collection };
        command.SetAction((parse, ct) => CliContext.RunAsync(parse, io, async (runtime, renderer) =>
        {
            if (!WireNames.Kinds.TryParse(parse.GetValue(kind)!, out var chosen))
            {
                io.Error.WriteLine($"error validation_failed: kind must be one of: {WireNames.Kinds.Joined}.");
                return ExitCodes.Usage;
            }

            if (!TryDestination(io, parse.GetValue(collection), out var destination))
            {
                return ExitCodes.Usage;
            }

            var result = await runtime.Decisions.ClassifyAsync(destination, chosen, parse.GetValue(title)!, parse.GetValue(summary)!, parse.GetValue(tags) ?? [], parse.GetValue(body), ct);
            if (!result.IsSuccess)
            {
                return CliContext.Fail(io, renderer, result.Error);
            }

            var text = new StringBuilder();
            if (result.Value.Suggestion is { } s)
            {
                text.Append(CultureInfo.InvariantCulture, $"{WireNames.Kinds.Name(s.Kind)} · confidence {s.Confidence:0.00} · {s.Source}{(s.TechnologySpecific == true ? " · technology-specific" : "")}\n");
                if (s.Disagreement(chosen, 0) is { } disagreement)
                {
                    text.Append("note: ").Append(disagreement).Append('\n');
                }
            }
            else
            {
                text.Append("no suggestion (the heuristic classifier defers to your choice)\n");
            }

            foreach (var note in result.Value.Notes)
            {
                text.Append("note: ").Append(note).Append('\n');
            }

            return CliContext.Write(io, text.ToString(), renderer);
        }, ct));
        return command;
    }

    private static Command Gate(CliIo io)
    {
        var text = new Argument<string>("text") { Description = "What you are considering remembering." };
        var collection = new Option<string?>("--collection") { Description = "Where it would be saved (default: writes.memory)." };
        var command = new Command("gate", "Ask the capture gate whether something is worth remembering.") { text, collection };
        command.SetAction((parse, ct) => CliContext.RunAsync(parse, io, async (runtime, renderer) =>
        {
            if (!TryDestination(io, parse.GetValue(collection), out var destination))
            {
                return ExitCodes.Usage;
            }

            var result = await runtime.Decisions.GateAsync(destination, parse.GetValue(text)!, ct);
            if (!result.IsSuccess)
            {
                return CliContext.Fail(io, renderer, result.Error);
            }

            var d = result.Value;
            var likelihood = d.Likelihood is { } p ? string.Create(CultureInfo.InvariantCulture, $" · {p:0.00}") : "";
            var output = new StringBuilder();
            output.Append(CultureInfo.InvariantCulture, $"{(d.Remember ? "remember" : "skip")}{likelihood} · {d.Source}\n");
            foreach (var note in d.Notes)
            {
                output.Append("note: ").Append(note).Append('\n');
            }

            return CliContext.Write(io, output.ToString(), renderer);
        }, ct));
        return command;
    }

    /// <summary>
    /// Sets <c>allowExternalDecisions</c> on a collection registration. The flag lives in the file that registers
    /// the collection (a higher file replaces a registration whole), so the collection must already be registered there.
    /// </summary>
    private static Command Switch(CliIo io, Option<bool> project, string name, bool value)
    {
        var id = new Argument<string>("collection") { Description = "A collection registered in the file being edited." };
        var command = new Command(name, value
            ? "Allow this collection's entry text to be sent to the decision model (default: never)."
            : "Stop sending this collection's entry text to the decision model.") { id, project };
        command.SetAction(parse =>
        {
            var collection = parse.GetValue(id)!;
            if (!CollectionId.IsValid(collection))
            {
                io.Error.WriteLine($"error validation_failed: '{collection}' is not a valid collection ID.");
                return ExitCodes.Usage;
            }

            var target = CollectionCommands.Target(io, parse, project);
            if (target is null)
            {
                return ExitCodes.Usage;
            }

            if (!Registered(target.Value.File, collection))
            {
                io.Error.WriteLine($"error validation_failed: {target.Value.File} does not register '{collection}'. Register it there first, or use {(target.Value.IsProject ? "the user file" : "--project")} where it is registered.");
                return ExitCodes.Usage;
            }

            var patch = new JsonObject { ["collections"] = new JsonObject { [collection] = new JsonObject { ["allowExternalDecisions"] = value } } };
            var change = FileEdits.MergeJson(target.Value.File, patch);
            if (change.Action == FileAction.Skipped)
            {
                io.Error.WriteLine($"error validation_failed: {target.Value.File}: {change.Detail}");
                return ExitCodes.Usage;
            }

            io.Out.WriteLine($"{change.Action.ToString().ToLowerInvariant()} {target.Value.File}: collection '{collection}' allowExternalDecisions = {(value ? "true" : "false")}");
            return ExitCodes.Success;
        });
        return command;
    }

    private static bool TryDestination(CliIo io, string? name, out CollectionId? destination)
    {
        destination = null;
        if (name is null)
        {
            return true;
        }

        if (!CollectionId.TryParse(name, out var id))
        {
            io.Error.WriteLine($"error validation_failed: '{name}' is not a valid collection ID.");
            return false;
        }

        destination = id;
        return true;
    }

    private static bool Registered(string file, string collection)
    {
        if (!File.Exists(file))
        {
            return false;
        }

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(file), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject;
            return root?["collections"] is JsonObject collections && collections[collection] is JsonObject;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
