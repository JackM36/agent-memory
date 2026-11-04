using System.Globalization;
using AgentBrain.Application.Abstractions.Proposals;
using AgentBrain.Application.Authorization;
using AgentBrain.Application.Common;
using AgentBrain.Application.Configuration;
using AgentBrain.Application.Indexing;
using AgentBrain.Application.Operations.Maintenance;
using AgentBrain.Application.Retrieval;
using AgentBrain.Application.Writing;
using AgentBrain.Composition;
using AgentBrain.Contracts.Rendering;
using AgentBrain.Contracts.V1;
using AgentBrain.Domain.Access;
using AgentBrain.Domain.Identity;
using AgentBrain.Domain.Revisions;

namespace AgentBrain.Server;

/// <summary>Protocol v1 routes (plan §10.4). Each maps onto the same operation the CLI uses.</summary>
internal static class Endpoints
{
    private static readonly JsonRenderer Json = new();

    public static void Map(WebApplication app)
    {
        app.MapGet("/health", (HttpContext c) => Http.WriteJsonAsync(c,
            new HealthDto("ok", ProtocolVersions.Protocol, typeof(Endpoints).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"),
            ContractsJsonContext.Default.HealthDto));

        app.MapGet("/v1/collections", Collections);
        app.MapPost("/v1/search", Search);
        app.MapPost("/v1/list", List);
        app.MapGet("/v1/entries/{collection}/{entry}", GetEntry);
        app.MapPost("/v1/entries:batchGet", BatchGet);
        app.MapGet("/v1/entries/{collection}/{entry}/references", References);
        app.MapGet("/v1/entries/{collection}/{entry}/backlinks", Backlinks);

        app.MapPost("/v1/entries", (HttpContext c) => Maintain(c, null, null, "create"));
        app.MapPut("/v1/entries/{collection}/{entry}", (HttpContext c, string collection, string entry) => Maintain(c, collection, entry, "update"));
        app.MapPost("/v1/entries/{collection}/{entry}/archive", (HttpContext c, string collection, string entry) => Maintain(c, collection, entry, "archive"));
        app.MapPost("/v1/entries/{collection}/{entry}/supersede", (HttpContext c, string collection, string entry) => Maintain(c, collection, entry, "supersede"));
        app.MapPost("/v1/entries/{collection}/{entry}/resolve", (HttpContext c, string collection, string entry) => Maintain(c, collection, entry, "resolve"));
        app.MapDelete("/v1/entries/{collection}/{entry}", Delete);

        app.MapPost("/v1/proposals", SubmitProposal);
        app.MapGet("/v1/proposals", ListProposals);
        app.MapGet("/v1/proposals/{id}", ShowProposal);
        app.MapPost("/v1/proposals/{id}/accept", (HttpContext c, string id) => Decide(c, id, accept: true));
        app.MapPost("/v1/proposals/{id}/reject", (HttpContext c, string id) => Decide(c, id, accept: false));
        app.MapPost("/v1/publish", Publish);

        app.MapPost("/v1/admin/reindex", (HttpContext c) => Admin(c, SyncMode.Incremental));
        app.MapPost("/v1/admin/verify", (HttpContext c) => Admin(c, SyncMode.Verify));
    }

    private static HostRuntime Runtime(HttpContext context) => context.RequestServices.GetRequiredService<HostRuntime>();

    /// <summary>Collections the caller may read; search and listing never see the others.</summary>
    private static List<CollectionId> Readable(HttpContext context) =>
        [.. Runtime(context).Catalog.All.Where(c => Http.Caller(context).Effective(c.Info).HasFlag(Capability.Read)).Select(c => c.Info.Id)];

    private static Task Collections(HttpContext context)
    {
        var caller = Http.Caller(context);
        var visible = Runtime(context).Catalog.All
            .Where(c => caller.Effective(c.Info).HasFlag(Capability.Read))
            .Select(c => c.Info with { Capabilities = caller.Effective(c.Info) })
            .ToList();
        return Http.WriteJsonAsync(context, Json.RenderCollections(visible));
    }

    private static async Task Search(HttpContext context)
    {
        var dto = await Http.ReadAsync(context, ContractsJsonContext.Default.SearchRequestDto);
        var input = dto.IsSuccess ? RequestMapper.ToSearchInput(dto.Value) : dto.Error;
        if (!input.IsSuccess)
        {
            await Http.WriteErrorAsync(context, input.Error);
            return;
        }

        var host = Runtime(context).Host;
        var readable = Readable(context);
        var collections = input.Value.Collections is { Count: > 0 } requested ? requested.Where(readable.Contains).ToList() : readable;
        if (collections.Count == 0)
        {
            await Http.WriteErrorAsync(context, new Error(ErrorCode.Forbidden, "This key cannot read any of the requested collections."));
            return;
        }

        var search = input.Value with
        {
            Collections = collections,
            Limit = Math.Min(input.Value.Limit ?? ConfigDefaults.MaxResults, host.MaxResults),
            MaxBytes = Math.Min(input.Value.MaxBytes ?? ConfigDefaults.MaxBytes, host.MaxBytes),
        };
        var response = await Runtime(context).Search.ExecuteAsync(search, Json, context.RequestAborted);
        await (response.IsSuccess ? Http.WriteJsonAsync(context, Json.RenderSearch(response.Value)) : Http.WriteErrorAsync(context, response.Error));
    }

    /// <summary>Provider-level listing: the cursor is this server's own position (the client keeps the federated cursor).</summary>
    private static async Task List(HttpContext context)
    {
        var dto = await Http.ReadAsync(context, ContractsJsonContext.Default.ListRequestDto);
        var input = dto.IsSuccess ? RequestMapper.ToListInput(dto.Value) : dto.Error;
        if (!input.IsSuccess)
        {
            await Http.WriteErrorAsync(context, input.Error);
            return;
        }

        var readable = Readable(context);
        var collections = input.Value.Collections is { Count: > 0 } requested ? requested.Where(readable.Contains).ToList() : readable;
        if (collections.Count == 0)
        {
            await Http.WriteErrorAsync(context, new Error(ErrorCode.Forbidden, "This key cannot read any of the requested collections."));
            return;
        }

        var runtime = Runtime(context);
        await new Application.Operations.IndexFreshness(runtime.Synchronizer).EnsureFreshAsync(context.RequestAborted);
        var request = new ListRequest(new SearchFilters(input.Value.Kinds, input.Value.Tags, input.Value.TagMatch, collections), input.Value.Include, input.Value.BlockingOnly, input.Value.Cursor, input.Value.PageSize);
        var page = await runtime.Provider.ListAsync(request, context.RequestAborted);
        await (page.IsSuccess
            ? Http.WriteJsonAsync(context, Json.RenderList(new ListResponse(page.Value.Items, page.Value.NextCursor, [new SourceStatus(runtime.Provider.SourceId, SourceState.Ok)], false)))
            : Http.WriteErrorAsync(context, page.Error));
    }

    private static async Task GetEntry(HttpContext context, string collection, string entry)
    {
        var section = context.Request.Query["section"].ToString();
        var text = $"{collection}/{entry}" + (section.Length > 0 ? "#" + section : "");
        if (!EntryRef.TryParse(text, out var target))
        {
            await Http.WriteErrorAsync(context, Error.Validation($"'{text}' is not collection-id/entry-id[#section]."));
            return;
        }

        int? maxBytes = int.TryParse(context.Request.Query["maxBytes"], NumberStyles.None, CultureInfo.InvariantCulture, out var m) ? m : null;
        await GetAsync(context, [target], maxBytes, context.Request.Query["withRefs"] == "true", single: true);
    }

    private static async Task BatchGet(HttpContext context)
    {
        var dto = await Http.ReadAsync(context, ContractsJsonContext.Default.BatchGetRequestDto);
        if (!dto.IsSuccess)
        {
            await Http.WriteErrorAsync(context, dto.Error);
            return;
        }

        var targets = new List<EntryRef>();
        foreach (var text in dto.Value.Refs)
        {
            if (!EntryRef.TryParse(text, out var target))
            {
                await Http.WriteErrorAsync(context, Error.Validation($"'{text}' is not collection-id/entry-id[#section]."));
                return;
            }

            targets.Add(target);
        }

        await GetAsync(context, targets, dto.Value.MaxBytes, dto.Value.WithReferences, single: false);
    }

    private static async Task GetAsync(HttpContext context, List<EntryRef> targets, int? maxBytes, bool withRefs, bool single)
    {
        var readable = Readable(context);
        if (targets.FirstOrDefault(t => !readable.Contains(t.Collection)) is { } denied)
        {
            // Unreadable collections are reported as missing, so a key cannot probe what exists (spec §6.3).
            await Http.WriteErrorAsync(context, Error.NotFound($"'{denied.WithoutAnchor()}' does not exist."));
            return;
        }

        var result = await Runtime(context).Get.ExecuteAsync(targets, maxBytes, withRefs, context.RequestAborted);
        if (!result.IsSuccess)
        {
            await Http.WriteErrorAsync(context, result.Error);
            return;
        }

        if (single && result.Value.Entries.Count == 0 && result.Value.Failures.Count > 0)
        {
            await Http.WriteErrorAsync(context, result.Value.Failures[0].Error);
            return;
        }

        await Http.WriteJsonAsync(context, Json.RenderEntries(result.Value.Entries, result.Value.Failures));
    }

    private static async Task References(HttpContext context, string collection, string entry)
    {
        if (!EntryRef.TryParse($"{collection}/{entry}", out var target) || !Readable(context).Contains(target.Collection))
        {
            await Http.WriteErrorAsync(context, Error.NotFound($"'{collection}/{entry}' does not exist."));
            return;
        }

        var result = await Runtime(context).Relations.ReferencesAsync(target, context.RequestAborted);
        await (result.IsSuccess ? Http.WriteJsonAsync(context, Json.RenderReferences(target, result.Value)) : Http.WriteErrorAsync(context, result.Error));
    }

    private static async Task Backlinks(HttpContext context, string collection, string entry)
    {
        if (!EntryRef.TryParse($"{collection}/{entry}", out var target) || !Readable(context).Contains(target.Collection))
        {
            await Http.WriteErrorAsync(context, Error.NotFound($"'{collection}/{entry}' does not exist."));
            return;
        }

        var pageSize = int.TryParse(context.Request.Query["pageSize"], NumberStyles.None, CultureInfo.InvariantCulture, out var p) ? p : PageDefaults.PageSize;
        var cursor = context.Request.Query["cursor"].ToString();
        var result = await Runtime(context).Relations.BacklinksAsync(target, cursor.Length > 0 ? cursor : null, pageSize, context.RequestAborted);
        if (!result.IsSuccess)
        {
            await Http.WriteErrorAsync(context, result.Error);
            return;
        }

        var readable = Readable(context);
        var visible = result.Value with { Items = [.. result.Value.Items.Where(b => readable.Contains(b.Source.Collection))] };
        await Http.WriteJsonAsync(context, Json.RenderBacklinks(target, visible));
    }

    private static async Task Maintain(HttpContext context, string? collection, string? entry, string action)
    {
        var dto = await Http.ReadAsync(context, ContractsJsonContext.Default.ChangeRequestDto);
        if (!dto.IsSuccess)
        {
            await Http.WriteErrorAsync(context, dto.Error);
            return;
        }

        var change = dto.Value with
        {
            Kind = action == "create" ? "create" : "update",
            Collection = collection ?? dto.Value.Collection,
            Entry = entry ?? dto.Value.Entry,
            Archive = action == "archive" || dto.Value.Archive,
        };
        var request = RequestMapper.ToChangeRequest(change);
        if (!request.IsSuccess)
        {
            await Http.WriteErrorAsync(context, request.Error);
            return;
        }

        var caller = Http.Caller(context);
        var maintenance = Runtime(context).Maintenance;
        var r = request.Value;
        var allow = (IReadOnlyCollection<string>?)change.AllowSecretRules ?? [];
        var target = r.Entry is null ? null : new EntryRef(r.Collection, r.Entry);
        Result<WriteResult> result = action switch
        {
            "create" => await maintenance.CreateAsync(caller, r.Collection, r.NewEntry!, allow, context.RequestAborted),
            "resolve" when r.Patch?.ResolvedBy is { } answer => await maintenance.ResolveAsync(caller, target!, answer, r.BaseRevision!, context.RequestAborted),
            _ => await maintenance.UpdateAsync(caller, target!, r.BaseRevision!, r.Patch!, allow, context.RequestAborted),
        };
        await (result.IsSuccess
            ? Http.WriteJsonAsync(context, WriteRendering.Written(result.Value, action, json: true), action == "create" ? StatusCodes.Status201Created : StatusCodes.Status200OK)
            : Http.WriteErrorAsync(context, result.Error));
    }

    private static async Task Delete(HttpContext context, string collection, string entry)
    {
        if (!EntryRef.TryParse($"{collection}/{entry}", out var target) || !Revision.TryParse(context.Request.Query["baseRevision"], out var revision))
        {
            await Http.WriteErrorAsync(context, Error.Validation("DELETE needs a valid entry and ?baseRevision=r1-…."));
            return;
        }

        var result = await Runtime(context).Maintenance.DeleteAsync(Http.Caller(context), target, revision, context.RequestAborted);
        await (result.IsSuccess ? Http.WriteJsonAsync(context, WriteRendering.Written(result.Value, "deleted", json: true)) : Http.WriteErrorAsync(context, result.Error));
    }

    private static async Task SubmitProposal(HttpContext context)
    {
        var dto = await Http.ReadAsync(context, ContractsJsonContext.Default.ChangeRequestDto);
        var request = dto.IsSuccess ? RequestMapper.ToChangeRequest(dto.Value) : dto.Error;
        if (!request.IsSuccess)
        {
            await Http.WriteErrorAsync(context, request.Error);
            return;
        }

        var runtime = Runtime(context);
        var caller = Http.Caller(context);
        if (runtime.Catalog.Find(request.Value.Collection) is not { } target)
        {
            await Http.WriteErrorAsync(context, Error.NotFound($"Collection '{request.Value.Collection}' is not hosted here."));
            return;
        }

        var allowed = CapabilityResolver.Require(caller, target.Info with { ReadOnly = false }, Capability.Propose);
        if (!allowed.IsSuccess)
        {
            await Http.WriteErrorAsync(context, allowed.Error);
            return;
        }

        var proposal = await runtime.Proposals.SubmitAsync(caller, request.Value, dto.Value.Note, (IReadOnlyCollection<string>?)dto.Value.AllowSecretRules ?? [], context.RequestAborted);
        await (proposal.IsSuccess
            ? Http.WriteJsonAsync(context, WriteRendering.Proposals([proposal.Value], json: true), StatusCodes.Status201Created)
            : Http.WriteErrorAsync(context, proposal.Error));
    }

    private static async Task ListProposals(HttpContext context)
    {
        ProposalStatus? status = Enum.TryParse<ProposalStatus>(context.Request.Query["status"], true, out var s) ? s : null;
        var readable = Readable(context);
        var proposals = (await Runtime(context).Proposals.ListAsync(status, context.RequestAborted))
            .Where(p => readable.Contains(p.Change.Target.Collection))
            .ToList();
        await Http.WriteJsonAsync(context, WriteRendering.Proposals(proposals, json: true));
    }

    private static async Task ShowProposal(HttpContext context, string id)
    {
        var proposal = await Runtime(context).Proposals.ShowAsync(id, context.RequestAborted);
        if (!proposal.IsSuccess || !Readable(context).Contains(proposal.Value.Change.Target.Collection))
        {
            await Http.WriteErrorAsync(context, Error.NotFound($"Proposal '{id}' does not exist."));
            return;
        }

        await Http.WriteJsonAsync(context, WriteRendering.Proposals([proposal.Value], json: true, withBody: true));
    }

    private static async Task Decide(HttpContext context, string id, bool accept)
    {
        var dto = context.Request.ContentLength is > 0 ? await Http.ReadAsync(context, ContractsJsonContext.Default.DecisionDto) : new DecisionDto();
        var note = dto.IsSuccess ? dto.Value.Note : null;
        var runtime = Runtime(context);
        var result = accept
            ? await runtime.Proposals.AcceptAsync(Http.Caller(context), id, note, context.RequestAborted)
            : await runtime.Proposals.RejectAsync(Http.Caller(context), id, note, context.RequestAborted);
        await (result.IsSuccess ? Http.WriteJsonAsync(context, WriteRendering.Proposals([result.Value], json: true)) : Http.WriteErrorAsync(context, result.Error));
    }

    private static async Task Publish(HttpContext context)
    {
        var dto = await Http.ReadAsync(context, ContractsJsonContext.Default.PublishRequestDto);
        if (!dto.IsSuccess)
        {
            await Http.WriteErrorAsync(context, dto.Error);
            return;
        }

        var requests = new List<Application.Operations.Proposals.ChangeRequest>();
        foreach (var change in dto.Value.Changes)
        {
            var request = RequestMapper.ToChangeRequest(change);
            if (!request.IsSuccess)
            {
                await Http.WriteErrorAsync(context, request.Error);
                return;
            }

            requests.Add(request.Value);
        }

        var result = await Runtime(context).Publishing.PublishAsync(Http.Caller(context), requests, [.. dto.Value.Changes.SelectMany(c => c.AllowSecretRules ?? []).Distinct(StringComparer.Ordinal)], context.RequestAborted);
        await (result.IsSuccess ? Http.WriteJsonAsync(context, WriteRendering.Published(result.Value, json: true)) : Http.WriteErrorAsync(context, result.Error));
    }

    private static async Task Admin(HttpContext context, SyncMode mode)
    {
        if (!ApiKeyService.IsAdmin(Http.Key(context).Scopes))
        {
            await Http.WriteErrorAsync(context, new Error(ErrorCode.Forbidden, "This operation needs an admin key."));
            return;
        }

        var report = await Runtime(context).Index.SynchronizeAsync(mode, context.RequestAborted);
        await (report.IsSuccess ? Http.WriteJsonAsync(context, Json.RenderIndex(report.Value)) : Http.WriteErrorAsync(context, report.Error));
    }
}
