using System.CommandLine;
using AgentBrain.Application.Common;
using AgentBrain.Application.Operations.Discovery;
using AgentBrain.Application.Retrieval;
using AgentBrain.Cli.Infrastructure;
using AgentBrain.Domain.Entries;
using AgentBrain.Domain.Identity;

namespace AgentBrain.Cli.Commands;

/// <summary><c>brain search</c>, <c>brain list</c> and <c>brain collections</c>.</summary>
internal static class DiscoveryCommands
{
    public static Command Search(CliIo io)
    {
        var query = new Argument<string>("query") { Description = "What you are working on, in a few words.", Arity = ArgumentArity.ZeroOrOne };
        var files = Many("--file", "A file you are working on (project-relative or absolute). Repeatable.");
        var symbols = Many("--symbol", "A code symbol you are working on. Repeatable.");
        var technologies = Many("--tech", "A technology to add to the project context. Repeatable.");
        var packages = Many("--package", "A package version as name@version (e.g. unity@2022.3.10f1). Repeatable.");
        var hints = Many("--hint-tag", "A tag that boosts matching entries without filtering. Repeatable.");
        var kinds = Many("--kind", $"Only these kinds ({WireNames.Kinds.Joined}). Repeatable.");
        var tags = Many("--tag", "Only entries with this tag (all given tags unless --tags-any). Repeatable.");
        var tagsAny = new Option<bool>("--tags-any") { Description = "Match any of the --tag values instead of all." };
        var collections = Many("--collection", "Only this collection. Repeatable.");
        var relax = new Option<string?>("--relax") { Description = "Admission: strict, default, broad, or a number in [0, 1]." };
        var limit = new Option<int?>("--limit") { Description = "Maximum results." };
        var maxTokens = new Option<int?>("--max-tokens") { Description = "Estimated token budget for the whole response." };
        var maxBytes = new Option<int?>("--max-bytes") { Description = "Hard byte limit for the whole response." };
        var history = new Option<bool>("--include-history") { Description = "Include archived and superseded entries and resolved uncertainties." };
        var incompatible = new Option<bool>("--include-incompatible") { Description = "Include entries whose version ranges exclude your package versions." };
        var explain = new Option<bool>("--explain") { Description = "Show the ranking evidence for every result." };

        var command = new Command("search", "Find relevant entries: a ranked, budgeted list to choose what to fetch.")
        {
            query, files, symbols, technologies, packages, hints, kinds, tags, tagsAny, collections, relax, limit, maxTokens, maxBytes, history, incompatible, explain,
        };

        command.SetAction((parse, cancellationToken) => CliContext.RunAsync(parse, io, async (runtime, renderer) =>
        {
            var parsed = ParseShared(parse.GetValue(kinds), parse.GetValue(collections));
            if (!parsed.IsSuccess)
            {
                return CliContext.Fail(io, renderer, parsed.Error);
            }

            var packageMap = ParsePackages(parse.GetValue(packages));
            if (!packageMap.IsSuccess)
            {
                return CliContext.Fail(io, renderer, packageMap.Error);
            }

            Relaxation? relaxation = null;
            if (parse.GetValue(relax) is { } relaxText)
            {
                if (!Relaxation.TryParse(relaxText, out var value))
                {
                    return CliContext.Fail(io, renderer, Error.Validation($"'{relaxText}' is not strict, default, broad or a number in [0, 1]."));
                }

                relaxation = value;
            }

            var input = new SearchInput(parse.GetValue(query) ?? "")
            {
                Files = parse.GetValue(files),
                Symbols = parse.GetValue(symbols),
                Technologies = parse.GetValue(technologies),
                Packages = packageMap.Value,
                HintTags = parse.GetValue(hints),
                Kinds = parsed.Value.Kinds,
                Tags = parse.GetValue(tags) is { Length: > 0 } t ? [.. t.Select(Names.NormalizeTag).OfType<string>()] : null,
                TagMatch = parse.GetValue(tagsAny) ? TagMatch.Any : TagMatch.All,
                Collections = parsed.Value.Collections,
                Relaxation = relaxation,
                Limit = parse.GetValue(limit),
                MaxTokens = parse.GetValue(maxTokens),
                MaxBytes = parse.GetValue(maxBytes),
                Include = new InclusionOptions(parse.GetValue(history), parse.GetValue(incompatible)),
                Explain = parse.GetValue(explain),
            };

            var response = await runtime.Search.ExecuteAsync(input, renderer, cancellationToken);
            return response.IsSuccess ? CliContext.Write(io, renderer.RenderSearch(response.Value), renderer) : CliContext.Fail(io, renderer, response.Error);
        }, cancellationToken));
        return command;
    }

    public static Command List(CliIo io)
    {
        var kinds = Many("--kind", $"Only these kinds ({WireNames.Kinds.Joined}). Repeatable.");
        var tags = Many("--tag", "Only entries with this tag (all given tags unless --tags-any). Repeatable.");
        var tagsAny = new Option<bool>("--tags-any") { Description = "Match any of the --tag values instead of all." };
        var collections = Many("--collection", "Only this collection. Repeatable.");
        var blocking = new Option<bool>("--blocking") { Description = "Only unresolved blocking uncertainties." };
        var history = new Option<bool>("--include-archived") { Description = "Include archived and superseded entries and resolved uncertainties." };
        var drafts = new Option<bool>("--include-drafts") { Description = "Include drafts." };
        var cursor = new Option<string?>("--cursor") { Description = "Continue from a previous page." };
        var pageSize = new Option<int>("--page-size") { Description = "Entries per page.", DefaultValueFactory = _ => PageDefaults.PageSize };

        var command = new Command("list", "List entries exactly, in stable order, page by page (no relevance cutoff).")
        {
            kinds, tags, tagsAny, collections, blocking, history, drafts, cursor, pageSize,
        };

        command.SetAction((parse, cancellationToken) => CliContext.RunAsync(parse, io, async (runtime, renderer) =>
        {
            var parsed = ParseShared(parse.GetValue(kinds), parse.GetValue(collections));
            if (!parsed.IsSuccess)
            {
                return CliContext.Fail(io, renderer, parsed.Error);
            }

            var input = new ListInput
            {
                Kinds = parsed.Value.Kinds,
                Tags = parse.GetValue(tags) is { Length: > 0 } t ? [.. t.Select(Names.NormalizeTag).OfType<string>()] : null,
                TagMatch = parse.GetValue(tagsAny) ? TagMatch.Any : TagMatch.All,
                Collections = parsed.Value.Collections,
                BlockingOnly = parse.GetValue(blocking),
                Include = new InclusionOptions(parse.GetValue(history), Incompatible: true, parse.GetValue(drafts)),
                Cursor = parse.GetValue(cursor),
                PageSize = parse.GetValue(pageSize),
            };
            var response = await runtime.List.ExecuteAsync(input, cancellationToken);
            return response.IsSuccess ? CliContext.Write(io, renderer.RenderList(response.Value), renderer) : CliContext.Fail(io, renderer, response.Error);
        }, cancellationToken));
        return command;
    }

    public static Command Collections(CliIo io)
    {
        var command = new Command("collections", "Show the configured collections and what you may do with each.");
        command.SetAction((parse, cancellationToken) => CliContext.RunAsync(parse, io, (runtime, renderer) =>
            Task.FromResult(CliContext.Write(io, renderer.RenderCollections(runtime.List.Collections()), renderer)), cancellationToken));
        return command;
    }

    internal static Option<string[]> Many(string name, string description) =>
        new(name) { Description = description, AllowMultipleArgumentsPerToken = false };

    private static Result<(IReadOnlyList<EntryKind>? Kinds, IReadOnlyList<CollectionId>? Collections)> ParseShared(string[]? kindNames, string[]? collectionNames)
    {
        List<EntryKind>? kinds = null;
        foreach (var name in kindNames ?? [])
        {
            if (!WireNames.Kinds.TryParse(name, out var kind))
            {
                return Error.Validation($"'{name}' is not one of: {WireNames.Kinds.Joined}.");
            }

            (kinds ??= []).Add(kind);
        }

        List<CollectionId>? collections = null;
        foreach (var name in collectionNames ?? [])
        {
            if (!CollectionId.TryParse(name, out var id))
            {
                return Error.Validation($"'{name}' is not a valid collection ID.");
            }

            (collections ??= []).Add(id);
        }

        return (kinds, collections);
    }

    private static Result<IReadOnlyDictionary<string, string>?> ParsePackages(string[]? values)
    {
        if (values is not { Length: > 0 })
        {
            return Result<IReadOnlyDictionary<string, string>?>.Success(null);
        }

        var packages = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var at = value.LastIndexOf('@');
            if (at <= 0 || at == value.Length - 1)
            {
                return Error.Validation($"'{value}' is not name@version.");
            }

            packages[value[..at].Trim().ToLowerInvariant()] = value[(at + 1)..].Trim();
        }

        return packages;
    }
}
