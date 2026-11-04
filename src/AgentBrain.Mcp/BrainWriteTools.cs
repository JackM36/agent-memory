using System.ComponentModel;
using AgentBrain.Application.Authorization;
using AgentBrain.Application.Common;
using AgentBrain.Application.Operations.Proposals;
using AgentBrain.Application.Operations.Routing;
using AgentBrain.Application.Writing;
using AgentBrain.Composition;
using AgentBrain.Contracts.Rendering;
using AgentBrain.Domain.Entries;
using AgentBrain.Domain.Identity;
using AgentBrain.Domain.Revisions;
using ModelContextProtocol.Server;

namespace AgentBrain.Mcp;

/// <summary>
/// MCP write tools (plan §7.3), all routed through <see cref="WriteRouter"/>. <c>save</c> writes to project memory or
/// the knowledge collection, local or remote, and becomes a proposal where the target only takes proposals;
/// <c>propose</c> records changes to curated collections explicitly; <c>publish</c> applies them immediately and is a separate, destructive-marked tool so
/// hosts can gate it by name on top of the explicit-approval rule in the agent guide. Deletion is CLI-only.
/// </summary>
[McpServerToolType]
public sealed class BrainWriteTools(BrainRuntime runtime)
{
    private static readonly CallerContext Caller = CallerContext.Local("mcp");

    [McpServerTool(Name = "save", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Save durable knowledge: action create, update, archive, supersede or resolve. Kind knowledge (reusable in any project, no project details) goes to the knowledge collection; other kinds go to project memory. A collection that only takes proposals turns the save into a proposal. Search first; update rather than duplicate. Never save secrets or guesses.")]
    public async Task<string> SaveAsync(
        [Description("create, update, archive, supersede or resolve.")] string action,
        [Description("update/archive/supersede/resolve: collection/entry.")] string? entry = null,
        [Description("update/archive/supersede/resolve: the revision from get.")] string? baseRevision = null,
        [Description("create: knowledge, memory, note, uncertainty or report.")] string? kind = null,
        [Description("Single line, at most 120 characters.")] string? title = null,
        [Description("Single line, at most 240 characters; shown in search results.")] string? summary = null,
        [Description("Markdown body.")] string? body = null,
        string[]? tags = null,
        [Description("Project-relative file globs this is about.")] string[]? files = null,
        string[]? symbols = null,
        [Description("Paths, tests or 'user confirmed <date>'.")] string[]? evidence = null,
        [Description("create uncertainty: guessing could cause harm.")] bool blocking = false,
        [Description("supersede: the replacement; resolve: the answering memory.")] string? by = null,
        [Description("create knowledge: technologies it is specific to, e.g. unity.")] string[]? technologies = null,
        [Description("create: target collection, only to override the default.")] string? collection = null,
        CancellationToken cancellationToken = default)
    {
        switch (action)
        {
            case "create":
                if (!WireNames.Kinds.TryParse(kind, out var entryKind) || title is null || summary is null)
                {
                    return Render(Error.Validation("create needs kind, title and summary."));
                }

                CollectionId? target = null;
                if (collection is not null && !CollectionId.TryParse(collection, out target))
                {
                    return Render(Error.Validation($"'{collection}' is not a valid collection ID."));
                }

                var newEntry = new NewEntry(entryKind, title, summary, body ?? "")
                {
                    Tags = tags ?? [], Files = files ?? [], Symbols = symbols ?? [], Evidence = evidence ?? [], Blocking = blocking,
                    Applies = technologies is { Length: > 0 } ? new ApplicabilityOverrides(Technologies: technologies) : null,
                };
                return Saved(await runtime.Writes.CreateAsync(Caller, target, newEntry, null, [], cancellationToken), "created");

            case "update" or "archive" or "supersede" or "resolve":
                if (!EntryRef.TryParse(entry, out var reference) || !Revision.TryParse(baseRevision, out var revision))
                {
                    return Render(Error.Validation($"{action} needs entry (collection/entry) and baseRevision from get."));
                }

                EntryRef? link = null;
                if (action is "supersede" or "resolve" && !EntryRef.TryParse(by, out link))
                {
                    return Render(Error.Validation($"{action} needs by (collection/entry)."));
                }

                var (maintenanceAction, patch) = action switch
                {
                    "archive" => (MaintenanceAction.Archive, new EntryPatch { Archive = true }),
                    "supersede" => (MaintenanceAction.Supersede, new EntryPatch { SupersededBy = link!.WithoutAnchor() }),
                    "resolve" => (MaintenanceAction.Resolve, new EntryPatch { ResolvedBy = link!.WithoutAnchor() }),
                    _ => (MaintenanceAction.Update, new EntryPatch { Title = title, Summary = summary, Body = body, Tags = tags, Files = files, Symbols = symbols, Evidence = evidence }),
                };
                return Saved(await runtime.Writes.ChangeAsync(Caller, maintenanceAction, reference, revision, patch, null, [], cancellationToken), action + "d");

            default:
                return Render(Error.Validation("action must be create, update, archive, supersede or resolve."));
        }
    }

    [McpServerTool(Name = "propose", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Propose a change to curated or read-only knowledge (kind create, update or delete). Nothing changes until a reviewer accepts it.")]
    public async Task<string> ProposeAsync(
        [Description("create, update or delete.")] string change,
        [Description("create: target collection; update/delete: collection/entry.")] string target,
        string? baseRevision = null,
        string? kind = null,
        string? title = null,
        string? summary = null,
        string? body = null,
        string[]? tags = null,
        [Description("Why, for the reviewer.")] string? note = null,
        CancellationToken cancellationToken = default)
    {
        var request = BuildRequest(change, target, baseRevision, kind, title, summary, body, tags);
        if (!request.IsSuccess)
        {
            return Render(request.Error);
        }

        var proposal = await runtime.Writes.ProposeAsync(Caller, request.Value, note, [], cancellationToken);
        return proposal.IsSuccess ? WriteRendering.Proposals([proposal.Value], json: false) : Render(proposal.Error);
    }

    [McpServerTool(Name = "publish", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Publish a change to curated knowledge immediately. Only after showing the user the exact change and receiving explicit approval; silence is not approval. Ask again if the change differs.")]
    public async Task<string> PublishAsync(
        [Description("create, update or delete.")] string change,
        [Description("create: target collection; update/delete: collection/entry.")] string target,
        string? baseRevision = null,
        string? kind = null,
        string? title = null,
        string? summary = null,
        string? body = null,
        string[]? tags = null,
        CancellationToken cancellationToken = default)
    {
        var request = BuildRequest(change, target, baseRevision, kind, title, summary, body, tags);
        if (!request.IsSuccess)
        {
            return Render(request.Error);
        }

        var published = await runtime.Writes.PublishAsync(Caller, [request.Value], [], cancellationToken);
        return published.IsSuccess ? WriteRendering.Published(published.Value, json: false) : Render(published.Error);
    }

    private static Result<ChangeRequest> BuildRequest(string change, string target, string? baseRevision, string? kind, string? title, string? summary, string? body, string[]? tags)
    {
        if (!Enum.TryParse<ChangeKind>(change, true, out var changeKind))
        {
            return Error.Validation("change must be create, update or delete.");
        }

        if (changeKind == ChangeKind.Create)
        {
            if (!CollectionId.TryParse(target, out var collection) || !WireNames.Kinds.TryParse(kind ?? "knowledge", out var entryKind) || title is null || summary is null)
            {
                return Error.Validation("create needs target (a collection), title and summary.");
            }

            return new ChangeRequest(ChangeKind.Create, collection) { NewEntry = new NewEntry(entryKind, title, summary, body ?? "") { Tags = tags ?? [] } };
        }

        if (!EntryRef.TryParse(target, out var reference) || !Revision.TryParse(baseRevision, out var revision))
        {
            return Error.Validation($"{change} needs target (collection/entry) and baseRevision.");
        }

        var request = new ChangeRequest(changeKind, reference.Collection) { Entry = reference.Entry, BaseRevision = revision };
        return changeKind == ChangeKind.Delete ? request : request with { Patch = new EntryPatch { Title = title, Summary = summary, Body = body, Tags = tags } };
    }

    private static string Saved(Result<SaveOutcome> result, string action) =>
        result.IsSuccess ? WriteRendering.Saved(result.Value, action, json: false) : Render(result.Error);

    private static string Render(Error error) => TextRenderer.Compact.RenderError(error);
}
