using AgentBrain.Application.Abstractions.Support;
using AgentBrain.Application.Common;
using AgentBrain.Application.Configuration;
using AgentBrain.Application.Retrieval;
using AgentBrain.Application.Retrieval.Budgeting;
using AgentBrain.Application.Retrieval.QueryAnalysis;
using AgentBrain.Domain.Entries;
using AgentBrain.Domain.Identity;

namespace AgentBrain.Application.Operations.Discovery;

/// <summary>Search as requested by a caller. Everything optional falls back to configuration, then built-in defaults.</summary>
public sealed record SearchInput(string Query)
{
    public IReadOnlyList<string>? Files { get; init; }

    public IReadOnlyList<string>? Symbols { get; init; }

    public IReadOnlyList<string>? Technologies { get; init; }

    public IReadOnlyDictionary<string, string>? Packages { get; init; }

    public IReadOnlyList<string>? HintTags { get; init; }

    public IReadOnlyList<EntryKind>? Kinds { get; init; }

    public IReadOnlyList<string>? Tags { get; init; }

    public TagMatch TagMatch { get; init; } = TagMatch.All;

    public IReadOnlyList<CollectionId>? Collections { get; init; }

    public Relaxation? Relaxation { get; init; }

    public int? Limit { get; init; }

    public int? MaxBytes { get; init; }

    public int? MaxTokens { get; init; }

    public InclusionOptions Include { get; init; } = InclusionOptions.Default;

    public bool Explain { get; init; }

    /// <summary>The caller's project when it is not this process's (remote requests that forward it).</summary>
    public string? ProjectId { get; init; }
}

/// <summary>Ranked, budgeted discovery across every configured source (spec §7.1, §7.7).</summary>
public sealed class SearchOperation(EffectiveConfiguration configuration, RetrievalCoordinator coordinator, IndexFreshness freshness, IQueryLog? queryLog = null)
{
    public async ValueTask<Result<SearchResponse>> ExecuteAsync(SearchInput input, IResponseRenderer renderer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(renderer);
        if (string.IsNullOrWhiteSpace(input.Query) && input.Files is not { Count: > 0 } && input.Symbols is not { Count: > 0 })
        {
            return Error.Validation("Give a query, files or symbols to search for.");
        }

        // A request problem, checked once here so it is not reported as every source failing.
        if (input.Query is { Length: > QueryAnalyzer.MaxQueryLength } query)
        {
            return Error.Validation($"The query is {query.Length} characters; the limit is {QueryAnalyzer.MaxQueryLength}.");
        }

        var settings = configuration.Search;
        var maxResults = Math.Clamp(input.Limit ?? settings.MaxResults, ConfigDefaults.MaxResultsRange.Min, ConfigDefaults.MaxResultsRange.Max);
        var request = new SearchRequest(
            input.Query ?? "",
            BuildContext(input),
            new SearchFilters(input.Kinds ?? settings.Kinds, input.Tags, input.TagMatch, input.Collections),
            input.Relaxation ?? settings.Relaxation,
            new ResponseLimits(maxResults, input.MaxBytes ?? settings.MaxBytes, input.MaxTokens ?? settings.MaxTokens),
            input.Include,
            Math.Max(settings.CandidatesPerSource, maxResults * 2),
            input.Explain);

        var stale = await freshness.EnsureFreshAsync(cancellationToken);
        var outcome = await coordinator.SearchAsync(request, stale, cancellationToken);
        if (outcome.Sources.Count > 0 && outcome.Sources.All(s => s.State != SourceState.Ok))
        {
            return new Error(ErrorCode.ProviderUnavailable, "No source could be searched: " + string.Join("; ", outcome.Sources.Select(s => s.Detail is null ? $"{s.SourceId} {s.State}" : $"{s.SourceId} {s.State} ({s.Detail})")));
        }

        var byteLimit = ResponseBudgeter.ByteLimit(request.Limits.MaxBytes, request.Limits.MaxTokens);
        var response = ResponseBudgeter.Fit(outcome, maxResults, byteLimit, renderer);
        if (response.IsSuccess && queryLog is not null)
        {
            await queryLog.AppendAsync(
                new QueryLogRecord(
                    DateTimeOffset.UtcNow,
                    request.Query,
                    request.Context,
                    [.. response.Value.Results.Select(r => r.Entry.Ref.ToString())],
                    [.. response.Value.Blocking.Select(r => r.Entry.Ref.ToString())]),
                cancellationToken);
        }

        return response;
    }

    private QueryContext BuildContext(SearchInput input)
    {
        var project = configuration.Project;
        var packages = new Dictionary<string, string>(project?.Packages ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        foreach (var (name, version) in input.Packages ?? new Dictionary<string, string>())
        {
            packages[name.Trim().ToLowerInvariant()] = version.Trim();
        }

        var technologies = (project?.Technologies ?? []).Concat((input.Technologies ?? []).Select(t => t.Trim().ToLowerInvariant())).Distinct(StringComparer.Ordinal).ToList();
        return new QueryContext(
            input.ProjectId ?? project?.Id,
            technologies,
            packages,
            ProjectPaths.Relative(project?.RootDirectory, input.Files),
            input.Symbols,
            input.HintTags?.Select(t => Names.NormalizeTag(t)).OfType<string>().ToList());
    }
}

/// <summary>Request file paths become project-relative; paths outside the project are dropped and never forwarded (plan §5.6).</summary>
public static class ProjectPaths
{
    public static IReadOnlyList<string>? Relative(string? projectRoot, IReadOnlyList<string>? files)
    {
        if (files is null)
        {
            return null;
        }

        var result = new List<string>();
        foreach (var file in files.Where(f => !string.IsNullOrWhiteSpace(f)))
        {
            if (!Path.IsPathRooted(file))
            {
                result.Add(file.Replace('\\', '/').TrimStart('.', '/'));
                continue;
            }

            if (projectRoot is null)
            {
                continue;
            }

            var relative = Path.GetRelativePath(projectRoot, file);
            if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
            {
                result.Add(relative.Replace('\\', '/'));
            }
        }

        return result;
    }
}
