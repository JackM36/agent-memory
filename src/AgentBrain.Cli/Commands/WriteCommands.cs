using System.CommandLine;
using System.Text.Json;
using AgentBrain.Application.Abstractions.Proposals;
using AgentBrain.Application.Authorization;
using AgentBrain.Application.Common;
using AgentBrain.Application.Operations.Maintenance;
using AgentBrain.Application.Operations.Proposals;
using AgentBrain.Application.Operations.Routing;
using AgentBrain.Application.Writing;
using AgentBrain.Cli.Infrastructure;
using AgentBrain.Composition;
using AgentBrain.Contracts.Rendering;
using AgentBrain.Domain.Entries;
using AgentBrain.Domain.Identity;
using AgentBrain.Domain.Revisions;

namespace AgentBrain.Cli.Commands;

/// <summary>
/// Write commands (plan §7.1): direct maintenance of project memory, proposals, immediate publishing and
/// batch recovery. Every one goes through the same application operations as MCP and HTTP.
/// </summary>
internal static class WriteCommands
{
    private static readonly CallerContext Caller = CallerContext.Local("cli");

    public static IEnumerable<Command> All(CliIo io) =>
    [
        Similar(io), New(io), Update(io), Lifecycle(io, "archive"), Lifecycle(io, "delete"), Link(io, "supersede"), Link(io, "resolve"),
        Propose(io), Proposals(io), Publish(io), Promote(io), Batch(io),
    ];

    private static Command Similar(CliIo io)
    {
        var title = new Option<string>("--title") { Required = true, Description = "Title of the entry you are about to write." };
        var summary = new Option<string>("--summary") { Description = "Its summary." };
        var tags = DiscoveryCommands.Many("--tag", "Its tags.");
        var collection = new Option<string?>("--collection") { Description = "Collection to check (default: writes.memory)." };
        var command = new Command("similar", "Find existing entries that may already cover what you are about to write.") { title, summary, tags, collection };
        command.SetAction((parse, ct) => CliContext.RunAsync(parse, io, async (runtime, renderer) =>
        {
            var id = ParseCollection(parse.GetValue(collection));
            if (!id.IsSuccess)
            {
                return CliContext.Fail(io, renderer, id.Error);
            }

            var result = await runtime.Maintenance.SimilarAsync(id.Value, parse.GetValue(title)!, parse.GetValue(summary) ?? "", parse.GetValue(tags) ?? [], ct);
            return result.IsSuccess ? CliContext.Write(io, WriteRendering.SimilarEntries(result.Value, IsJson(renderer)), renderer) : CliContext.Fail(io, renderer, result.Error);
        }, ct));
        return command;
    }

    private static Command New(CliIo io)
    {
        var kind = new Argument<string>("kind") { Description = $"Entry kind: {WireNames.Kinds.Joined}." };
        var options = new EntryOptions();
        var collection = new Option<string?>("--collection") { Description = "Target collection (default: writes.knowledge for knowledge, else writes.memory)." };
        var id = new Option<string?>("--id") { Description = "Entry ID (default: generated from the title)." };
        var blocking = new Option<bool>("--blocking") { Description = "Uncertainties only: guessing could cause harm, so agents must ask first." };
        var command = new Command("new", "Create an entry. Knowledge goes to writes.knowledge, other kinds to writes.memory; a collection that only takes proposals receives a proposal.") { kind, collection, id, blocking, options.Technologies, options.AllowSecretRules };
        foreach (var option in options.Content)
        {
            command.Options.Add(option);
        }

        command.SetAction((parse, ct) => CliContext.RunAsync(parse, io, async (runtime, renderer) =>
        {
            var entry = await BuildNewEntryAsync(parse, io, kind, options, id, blocking);
            var target = ParseCollection(parse.GetValue(collection));
            if (!entry.IsSuccess || !target.IsSuccess)
            {
                return CliContext.Fail(io, renderer, entry.Error ?? target.Error!);
            }

            var result = await runtime.Writes.CreateAsync(Caller, target.Value, entry.Value, null, parse.GetValue(options.AllowSecretRules) ?? [], ct);
            return Saved(io, renderer, result, "created");
        }, ct));
        return command;
    }

    private static Command Update(CliIo io)
    {
        var entry = new Argument<string>("ref") { Description = "collection-id/entry-id" };
        var baseRevision = BaseRevisionOption();
        var options = new EntryOptions();
        var command = new Command("update", "Change an entry. Only the given fields change; the base revision guards against overwriting newer edits.") { entry, baseRevision, options.AllowSecretRules };
        foreach (var option in options.Content)
        {
            command.Options.Add(option);
        }

        command.SetAction((parse, ct) => CliContext.RunAsync(parse, io, async (runtime, renderer) =>
        {
            var target = EntryOptions.ParseRef(parse.GetValue(entry));
            var revision = EntryOptions.ParseRevision(parse.GetValue(baseRevision));
            var patch = await options.PatchAsync(parse, io);
            if (!target.IsSuccess || !revision.IsSuccess || !patch.IsSuccess)
            {
                return CliContext.Fail(io, renderer, target.Error ?? revision.Error ?? patch.Error!);
            }

            var result = await runtime.Writes.ChangeAsync(Caller, MaintenanceAction.Update, target.Value, revision.Value, patch.Value, null, parse.GetValue(options.AllowSecretRules) ?? [], ct);
            return Saved(io, renderer, result, "updated");
        }, ct));
        return command;
    }

    private static Command Lifecycle(CliIo io, string name)
    {
        var entry = new Argument<string>("ref") { Description = "collection-id/entry-id" };
        var baseRevision = BaseRevisionOption();
        var command = new Command(name, name == "archive"
            ? "Retire an entry from default search (it stays listable and fetchable)."
            : "Delete an entry permanently (prefer archive).") { entry, baseRevision };
        command.SetAction((parse, ct) => CliContext.RunAsync(parse, io, async (runtime, renderer) =>
        {
            var target = EntryOptions.ParseRef(parse.GetValue(entry));
            var revision = EntryOptions.ParseRevision(parse.GetValue(baseRevision));
            if (!target.IsSuccess || !revision.IsSuccess)
            {
                return CliContext.Fail(io, renderer, target.Error ?? revision.Error!);
            }

            var result = name == "archive"
                ? await runtime.Writes.ChangeAsync(Caller, MaintenanceAction.Archive, target.Value, revision.Value, new EntryPatch { Archive = true }, null, [], ct)
                : await runtime.Writes.DeleteAsync(Caller, target.Value, revision.Value, null, ct);
            return Saved(io, renderer, result, name == "archive" ? "archived" : "deleted");
        }, ct));
        return command;
    }

    private static Command Link(CliIo io, string name)
    {
        var entry = new Argument<string>("ref") { Description = name == "resolve" ? "The uncertainty: collection-id/entry-id" : "collection-id/entry-id" };
        var by = new Option<string>("--by") { Required = true, Description = name == "resolve" ? "The memory that answers it." : "The replacement entry." };
        var baseRevision = BaseRevisionOption();
        var command = new Command(name, name == "resolve"
            ? "Mark an uncertainty resolved by a memory; it stops blocking and leaves default search."
            : "Mark an entry superseded by another; it leaves default search and points to the replacement.") { entry, by, baseRevision };
        command.SetAction((parse, ct) => CliContext.RunAsync(parse, io, async (runtime, renderer) =>
        {
            var target = EntryOptions.ParseRef(parse.GetValue(entry));
            var link = EntryOptions.ParseRef(parse.GetValue(by));
            var revision = EntryOptions.ParseRevision(parse.GetValue(baseRevision));
            if (!target.IsSuccess || !link.IsSuccess || !revision.IsSuccess)
            {
                return CliContext.Fail(io, renderer, target.Error ?? link.Error ?? revision.Error!);
            }

            var result = name == "resolve"
                ? await runtime.Writes.ChangeAsync(Caller, MaintenanceAction.Resolve, target.Value, revision.Value, new EntryPatch { ResolvedBy = link.Value }, null, [], ct)
                : await runtime.Writes.ChangeAsync(Caller, MaintenanceAction.Supersede, target.Value, revision.Value, new EntryPatch { SupersededBy = link.Value }, null, [], ct);
            return Saved(io, renderer, result, name == "resolve" ? "resolved" : "superseded");
        }, ct));
        return command;
    }

    private static Command Propose(CliIo io)
    {
        var command = new Command("propose", "Propose a change to a curated or read-only collection. Nothing changes until it is accepted.");
        foreach (var kind in new[] { ChangeKind.Create, ChangeKind.Update, ChangeKind.Delete })
        {
            command.Subcommands.Add(ChangeCommand(io, kind, publish: false));
        }

        return command;
    }

    private static Command Publish(CliIo io)
    {
        var batch = new Option<string?>("--batch") { Description = "A JSON file of changes to one collection, applied together (see docs/cli.md)." };
        var allow = DiscoveryCommands.Many("--allow-secret-rule", "Allow one secret-scan rule for these writes.");
        var command = new Command("publish", "Submit and publish immediately. Only where the collection enables it, and only with the user's explicit approval of the exact change.") { batch, allow };
        foreach (var kind in new[] { ChangeKind.Create, ChangeKind.Update, ChangeKind.Delete })
        {
            command.Subcommands.Add(ChangeCommand(io, kind, publish: true));
        }

        command.SetAction((parse, ct) => CliContext.RunAsync(parse, io, async (runtime, renderer) =>
        {
            if (parse.GetValue(batch) is not { } file)
            {
                return CliContext.Fail(io, renderer, Error.Validation("Use publish create|update|delete, or --batch <file>."));
            }

            var requests = await ReadBatchAsync(Path.GetFullPath(file, io.WorkingDirectory));
            if (!requests.IsSuccess)
            {
                return CliContext.Fail(io, renderer, requests.Error);
            }

            var result = await runtime.Writes.PublishAsync(Caller, requests.Value, parse.GetValue(allow) ?? [], ct);
            return result.IsSuccess ? CliContext.Write(io, WriteRendering.Published(result.Value, IsJson(renderer)), renderer) : CliContext.Fail(io, renderer, result.Error);
        }, ct));
        return command;
    }

    private static Command ChangeCommand(CliIo io, ChangeKind kind, bool publish)
    {
        var name = kind.ToString().ToLowerInvariant();
        var options = new EntryOptions();
        var collection = new Option<string?>("--collection") { Description = "Target collection (create; default: writes.knowledge for knowledge, else writes.memory)." };
        var entryKind = new Option<string>("--kind") { Description = $"Entry kind for create: {WireNames.Kinds.Joined}.", DefaultValueFactory = _ => "knowledge" };
        var id = new Option<string?>("--id") { Description = "Entry ID for create (default: from the title)." };
        var entry = new Argument<string?>("ref") { Description = "collection-id/entry-id (update and delete).", Arity = ArgumentArity.ZeroOrOne };
        var baseRevision = new Option<string?>("--base-revision") { Description = "The revision your change is based on (update and delete)." };
        var archive = new Option<bool>("--archive") { Description = "Update: archive the entry." };
        var note = new Option<string?>("--note") { Description = "Why: shown to the reviewer." };
        var command = new Command(name, $"{(publish ? "Publish" : "Propose")} a {name}.") { entry, collection, entryKind, id, baseRevision, archive, options.AllowSecretRules };
        if (!publish)
        {
            command.Options.Add(note);
        }

        if (kind != ChangeKind.Delete)
        {
            foreach (var option in options.Content)
            {
                command.Options.Add(option);
            }
        }

        if (kind == ChangeKind.Create)
        {
            command.Options.Add(options.Technologies);
        }

        command.SetAction((parse, ct) => CliContext.RunAsync(parse, io, async (runtime, renderer) =>
        {
            var request = await BuildRequestAsync(parse, io, runtime, kind, options, collection, entryKind, id, entry, baseRevision, archive);
            if (!request.IsSuccess)
            {
                return CliContext.Fail(io, renderer, request.Error);
            }

            var allowed = parse.GetValue(options.AllowSecretRules) ?? [];
            if (publish)
            {
                var published = await runtime.Writes.PublishAsync(Caller, [request.Value], allowed, ct);
                return published.IsSuccess ? CliContext.Write(io, WriteRendering.Published(published.Value, IsJson(renderer)), renderer) : CliContext.Fail(io, renderer, published.Error);
            }

            var proposal = await runtime.Writes.ProposeAsync(Caller, request.Value, parse.GetValue(note), allowed, ct);
            return proposal.IsSuccess ? CliContext.Write(io, WriteRendering.Proposals([proposal.Value], IsJson(renderer)), renderer) : CliContext.Fail(io, renderer, proposal.Error);
        }, ct));
        return command;
    }

    private static Command Proposals(CliIo io)
    {
        var status = new Option<string?>("--status") { Description = "pending, accepted or rejected." };
        var list = new Command("list", "List proposals, local and on every connection. Remote IDs read connection:id.") { status };
        list.SetAction((parse, ct) => CliContext.RunAsync(parse, io, async (runtime, renderer) =>
        {
            ProposalStatus? filter = parse.GetValue(status) is { } s && Enum.TryParse<ProposalStatus>(s, true, out var parsed) ? parsed : null;
            var listing = await runtime.Writes.ListProposalsAsync(filter, ct);
            return CliContext.Write(io, WriteRendering.Proposals(listing.Proposals, IsJson(renderer), unavailable: listing.Unavailable), renderer);
        }, ct));

        var id = new Argument<string>("id");
        var show = new Command("show", "Show a proposal with its proposed content.") { id };
        show.SetAction((parse, ct) => CliContext.RunAsync(parse, io, async (runtime, renderer) =>
        {
            var proposal = await runtime.Writes.ShowProposalAsync(parse.GetValue(id)!, ct);
            return proposal.IsSuccess ? CliContext.Write(io, WriteRendering.Proposals([proposal.Value], IsJson(renderer), withBody: true), renderer) : CliContext.Fail(io, renderer, proposal.Error);
        }, ct));

        var note = new Option<string?>("--note") { Description = "Review note." };
        var acceptId = new Argument<string>("id");
        var accept = new Command("accept", "Apply a pending proposal (requires publish on the target).") { acceptId, note };
        accept.SetAction((parse, ct) => CliContext.RunAsync(parse, io, async (runtime, renderer) =>
        {
            var result = await runtime.Writes.DecideAsync(Caller, parse.GetValue(acceptId)!, accept: true, parse.GetValue(note), ct);
            return result.IsSuccess ? CliContext.Write(io, WriteRendering.Proposals([result.Value], IsJson(renderer)), renderer) : CliContext.Fail(io, renderer, result.Error);
        }, ct));

        var reason = new Option<string?>("--reason") { Description = "Why it was rejected." };
        var rejectId = new Argument<string>("id");
        var reject = new Command("reject", "Reject a pending proposal.") { rejectId, reason };
        reject.SetAction((parse, ct) => CliContext.RunAsync(parse, io, async (runtime, renderer) =>
        {
            var result = await runtime.Writes.DecideAsync(Caller, parse.GetValue(rejectId)!, accept: false, parse.GetValue(reason), ct);
            return result.IsSuccess ? CliContext.Write(io, WriteRendering.Proposals([result.Value], IsJson(renderer)), renderer) : CliContext.Fail(io, renderer, result.Error);
        }, ct));

        return new Command("proposals", "Review proposals.") { list, show, accept, reject };
    }

    /// <summary>
    /// Copies an entry into the knowledge collection as reusable knowledge. This is mechanical: it keeps the text and
    /// drops project-specific files and symbols. Rewriting the text for general use is the agent's or your job
    /// (pass --title, --summary or --body-file); the MCP prompt <c>promote</c> asks an agent to do that review.
    /// </summary>
    private static Command Promote(CliIo io)
    {
        var entry = new Argument<string>("ref") { Description = "The entry to promote, e.g. game.memory/jsonutility-dictionaries." };
        var to = new Option<string?>("--to") { Description = "Target collection (default: writes.knowledge)." };
        var options = new EntryOptions();
        var supersede = new Option<bool>("--supersede") { Description = "Mark the original superseded by the copy (only when the copy is written, not proposed)." };
        var command = new Command("promote", "Copy an entry into the knowledge collection as general knowledge, directly or as a proposal.")
        {
            entry, to, options.Title, options.Summary, options.Body, options.BodyFile, options.Tags, options.Technologies, supersede, options.AllowSecretRules,
        };
        command.SetAction((parse, ct) => CliContext.RunAsync(parse, io, async (runtime, renderer) =>
        {
            var source = EntryOptions.ParseRef(parse.GetValue(entry));
            var target = ParseCollection(parse.GetValue(to));
            if (!source.IsSuccess || !target.IsSuccess)
            {
                return CliContext.Fail(io, renderer, source.Error ?? target.Error!);
            }

            var destination = target.Value ?? runtime.Configuration.KnowledgeCollection;
            if (destination is null)
            {
                return CliContext.Fail(io, renderer, Error.Validation("Give --to, or set writes.knowledge (brain collection create library --knowledge)."));
            }

            var fetched = await runtime.Get.ExecuteAsync([source.Value], null, false, ct);
            if (!fetched.IsSuccess || fetched.Value.Entries.Count == 0)
            {
                return CliContext.Fail(io, renderer, fetched.IsSuccess ? fetched.Value.Failures[0].Error : fetched.Error);
            }

            var original = fetched.Value.Entries[0];
            if (original.OmittedBytes > 0)
            {
                return CliContext.Fail(io, renderer, Error.Validation("The entry is larger than one get returns; pass --body-file with the text to promote."));
            }

            var body = await options.ReadBodyAsync(parse, io);
            if (!body.IsSuccess)
            {
                return CliContext.Fail(io, renderer, body.Error);
            }

            var tags = parse.GetValue(options.Tags) is { Length: > 0 } given ? given : original.Entry.Tags;
            var technologies = parse.GetValue(options.Technologies) is { Length: > 0 } t ? t : original.Entry.Applicability.Technologies;
            var copy = new NewEntry(EntryKind.Knowledge, parse.GetValue(options.Title) ?? original.Entry.Title, parse.GetValue(options.Summary) ?? original.Entry.Summary, body.Value ?? original.Body)
            {
                Tags = tags,
                Evidence = [$"promoted from {source.Value}"],
                Applies = technologies.Count > 0 ? new ApplicabilityOverrides(Technologies: technologies) : null,
            };

            var saved = await runtime.Writes.CreateAsync(Caller, destination, copy, $"Promoted from {source.Value}", parse.GetValue(options.AllowSecretRules) ?? [], ct);
            if (!saved.IsSuccess)
            {
                return CliContext.Fail(io, renderer, saved.Error);
            }

            var output = new System.Text.StringBuilder(WriteRendering.Saved(saved.Value, "promoted", IsJson(renderer)));
            if (parse.GetValue(supersede) && saved.Value.Written is { } written)
            {
                var superseded = await runtime.Writes.ChangeAsync(
                    Caller, MaintenanceAction.Supersede, source.Value, original.Entry.Revision, new EntryPatch { SupersededBy = written.Ref }, null, [], ct);
                if (!superseded.IsSuccess)
                {
                    return CliContext.Fail(io, renderer, superseded.Error);
                }

                if (!IsJson(renderer))
                {
                    output.Append(WriteRendering.Saved(superseded.Value, "superseded", json: false));
                }
            }

            return CliContext.Write(io, output.ToString(), renderer);
        }, ct));
        return command;
    }

    private static Command Batch(CliIo io)
    {
        var status = new Command("status", "Show interrupted multi-file publications and which changes were applied.");
        status.SetAction((parse, ct) => CliContext.RunAsync(parse, io, async (runtime, renderer) =>
            CliContext.Write(io, WriteRendering.Batches(await runtime.Publishing.StatusAsync(ct), IsJson(renderer)), renderer), ct));

        var resumeId = new Argument<string>("id");
        var resume = new Command("resume", "Apply the remaining changes of an interrupted batch (re-checks every revision).") { resumeId };
        resume.SetAction((parse, ct) => CliContext.RunAsync(parse, io, async (runtime, renderer) =>
        {
            var result = await runtime.Publishing.ResumeAsync(Caller, parse.GetValue(resumeId)!, ct);
            return result.IsSuccess ? CliContext.Write(io, WriteRendering.Batches([result.Value], IsJson(renderer)), renderer) : CliContext.Fail(io, renderer, result.Error);
        }, ct));

        var discardId = new Argument<string>("id");
        var discard = new Command("discard", "Forget an interrupted batch after restoring the files yourself (e.g. with Git).") { discardId };
        discard.SetAction((parse, ct) => CliContext.RunAsync(parse, io, async (runtime, renderer) =>
        {
            await runtime.Publishing.DiscardAsync(parse.GetValue(discardId)!, ct);
            return CliContext.Write(io, "discarded\n", renderer);
        }, ct));

        return new Command("batch", "Inspect and recover interrupted publications.") { status, resume, discard };
    }

    private static async Task<Result<NewEntry>> BuildNewEntryAsync(ParseResult parse, CliIo io, Argument<string> kind, EntryOptions options, Option<string?> id, Option<bool> blocking)
    {
        if (!WireNames.Kinds.TryParse(parse.GetValue(kind), out var entryKind))
        {
            return Error.Validation($"Kind must be one of: {WireNames.Kinds.Joined}.");
        }

        EntryId? entryId = null;
        if (parse.GetValue(id) is { } idText && !EntryId.TryParse(idText, out entryId))
        {
            return Error.Validation($"'{idText}' is not a valid entry ID.");
        }

        return await options.NewEntryAsync(parse, io, entryKind, parse.GetValue(blocking), entryId);
    }

    private static async Task<Result<ChangeRequest>> BuildRequestAsync(
        ParseResult parse, CliIo io, BrainRuntime runtime, ChangeKind kind, EntryOptions options,
        Option<string?> collection, Option<string> entryKind, Option<string?> id, Argument<string?> entry, Option<string?> baseRevision, Option<bool> archive)
    {
        if (kind == ChangeKind.Create)
        {
            if (!WireNames.Kinds.TryParse(parse.GetValue(entryKind), out var k))
            {
                return Error.Validation($"--kind must be one of: {WireNames.Kinds.Joined}.");
            }

            var target = ParseCollection(parse.GetValue(collection)) is { IsSuccess: true } c ? c.Value ?? runtime.Writes.DefaultTarget(k) : null;
            if (target is null)
            {
                return Error.Validation("Give --collection (no writes.knowledge or writes.memory is configured).");
            }

            EntryId? entryId = null;
            if (parse.GetValue(id) is { } idText && !EntryId.TryParse(idText, out entryId))
            {
                return Error.Validation($"'{idText}' is not a valid entry ID.");
            }

            var entryResult = await options.NewEntryAsync(parse, io, k, blocking: false, entryId);
            return entryResult.IsSuccess ? new ChangeRequest(ChangeKind.Create, target) { NewEntry = entryResult.Value } : entryResult.Error;
        }

        var reference = EntryOptions.ParseRef(parse.GetValue(entry));
        var revision = EntryOptions.ParseRevision(parse.GetValue(baseRevision));
        if (!reference.IsSuccess || !revision.IsSuccess)
        {
            return reference.Error ?? revision.Error!;
        }

        var request = new ChangeRequest(kind, reference.Value.Collection) { Entry = reference.Value.Entry, BaseRevision = revision.Value };
        if (kind == ChangeKind.Delete)
        {
            return request;
        }

        var patch = await options.PatchAsync(parse, io);
        return patch.IsSuccess ? request with { Patch = patch.Value with { Archive = parse.GetValue(archive) } } : patch.Error;
    }

    /// <summary>Reads a batch file: <c>{ "changes": [ { "kind": "create|update|delete", "collection": …, … } ] }</c>.</summary>
    internal static async Task<Result<IReadOnlyList<ChangeRequest>>> ReadBatchAsync(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var requests = new List<ChangeRequest>();
            foreach (var change in document.RootElement.GetProperty("changes").EnumerateArray())
            {
                var parsed = ParseChange(change);
                if (!parsed.IsSuccess)
                {
                    return parsed.Error;
                }

                requests.Add(parsed.Value);
            }

            return requests;
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return Error.Validation($"Cannot read the batch file: {ex.Message}");
        }
    }

    private static Result<ChangeRequest> ParseChange(JsonElement change)
    {
        string? Text(string name) => change.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        List<string>? List(string name) => change.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? [.. v.EnumerateArray().Select(x => x.GetString()!)] : null;

        if (!Enum.TryParse<ChangeKind>(Text("kind"), true, out var kind) || !CollectionId.TryParse(Text("collection"), out var collection))
        {
            return Error.Validation("Each change needs 'kind' (create, update, delete) and a valid 'collection'.");
        }

        if (kind == ChangeKind.Create)
        {
            if (!WireNames.Kinds.TryParse(Text("entryKind") ?? "knowledge", out var entryKind) || Text("title") is not { } title || Text("summary") is not { } summary)
            {
                return Error.Validation("A create needs 'title', 'summary' and a valid 'entryKind'.");
            }

            var id = EntryId.TryParse(Text("entry"), out var parsedId) ? parsedId : null;
            return new ChangeRequest(kind, collection)
            {
                NewEntry = new NewEntry(entryKind, title, summary, Text("body") ?? "")
                {
                    Id = id, Tags = List("tags") ?? [], Files = List("files") ?? [], Symbols = List("symbols") ?? [], Evidence = List("evidence") ?? [],
                    Applies = List("technologies") is { Count: > 0 } technologies ? new ApplicabilityOverrides(Technologies: technologies) : null,
                },
            };
        }

        if (!EntryId.TryParse(Text("entry"), out var entry) || !Revision.TryParse(Text("baseRevision"), out var revision))
        {
            return Error.Validation("Updates and deletes need 'entry' and 'baseRevision'.");
        }

        var request = new ChangeRequest(kind, collection) { Entry = entry, BaseRevision = revision };
        return kind == ChangeKind.Delete ? request : request with
        {
            Patch = new EntryPatch
            {
                Title = Text("title"), Summary = Text("summary"), Body = Text("body"), Tags = List("tags"), Files = List("files"),
                Symbols = List("symbols"), Evidence = List("evidence"),
                Archive = change.TryGetProperty("archive", out var a) && a.GetBoolean(),
            },
        };
    }

    private static Result<CollectionId?> ParseCollection(string? text) => text is null
        ? Result<CollectionId?>.Success(null)
        : CollectionId.TryParse(text, out var id) ? id : Error.Validation($"'{text}' is not a valid collection ID.");

    private static Option<string> BaseRevisionOption() =>
        new("--base-revision") { Required = true, Description = "The revision you read (from brain get). Stale revisions are rejected." };

    private static bool IsJson(Application.Abstractions.Support.IResponseRenderer renderer) => renderer is JsonRenderer;

    private static int Saved(CliIo io, Application.Abstractions.Support.IResponseRenderer renderer, Result<SaveOutcome> result, string action) =>
        result.IsSuccess ? CliContext.Write(io, WriteRendering.Saved(result.Value, action, IsJson(renderer)), renderer) : CliContext.Fail(io, renderer, result.Error);
}
