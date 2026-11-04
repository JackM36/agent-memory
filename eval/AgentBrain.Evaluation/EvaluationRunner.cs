using System.Diagnostics;
using System.Text;
using AgentBrain.Application.Retrieval;
using AgentBrain.Composition;
using AgentBrain.Contracts.Rendering;
using AgentBrain.Domain.Entries;
using AgentBrain.Infrastructure.Local.Configuration;
using AgentBrain.Infrastructure.Sqlite;
using SearchInput = AgentBrain.Application.Operations.Discovery.SearchInput;

namespace AgentBrain.Evaluation;

internal sealed record VariantResult(
    string CaseId,
    string Variant,
    IReadOnlyList<string> Categories,
    bool Passed,
    IReadOnlyList<string> Failures,
    int RelevantExpected,
    int RelevantFound,
    int IrrelevantListed,
    int IrrelevantFound,
    int ResultCount,
    int Bytes,
    double Milliseconds,
    IReadOnlyList<string> Results,
    IReadOnlyList<string> Blocking);

internal sealed record MonotonicityViolation(string CaseId, IReadOnlyList<string> LostAtBroad);

/// <summary>
/// Runs every Phase 2 case against the corpus through the real runtime (configuration, Markdown,
/// SQLite index, ranking, budgeting, compact rendering). Each named context becomes a temporary project.
/// </summary>
internal sealed class EvaluationRunner(string evalRoot, IndexTokenizer tokenizer, int phase)
{
    private static readonly string[] CorpusCollections = ["general.engineering", "unity.kb", "web.kb", "space-game.memory", "web-shop.memory"];

    public async Task<(IReadOnlyList<VariantResult> Results, IReadOnlyList<MonotonicityViolation> Monotonicity)> RunAsync(CancellationToken cancellationToken)
    {
        var casesDirectory = Path.Combine(evalRoot, "cases");
        var contexts = CaseLoader.LoadContexts(casesDirectory).ToDictionary(c => c.Name);
        var cases = CaseLoader.LoadCases(casesDirectory).Where(c => c.Phase <= phase).ToList();
        var workspace = Directory.CreateTempSubdirectory("brain-eval-").FullName;
        var results = new List<VariantResult>();
        var violations = new List<MonotonicityViolation>();

        try
        {
            foreach (var group in cases.GroupBy(c => (c.Context, Sources: string.Join(',', c.Sources))))
            {
                var extraSources = group.Key.Sources.Split(',').Where(s => s != "corpus").ToList();
                using var runtime = await OpenAsync(workspace, contexts[group.Key.Context], extraSources, cancellationToken);
                await runtime.Index.SynchronizeAsync(Application.Indexing.SyncMode.Rebuild, cancellationToken);
                foreach (var testCase in group)
                {
                    foreach (var variant in testCase.Variants)
                    {
                        results.Add(await RunVariantAsync(runtime, testCase, variant, cancellationToken));
                    }

                    if (await CheckMonotonicityAsync(runtime, testCase, cancellationToken) is { } violation)
                    {
                        violations.Add(violation);
                    }
                }
            }
        }
        finally
        {
            try
            {
                Directory.Delete(workspace, recursive: true);
            }
            catch (IOException)
            {
                // Best effort: the OS temp cleaner removes leftovers.
            }
        }

        return (results, violations);
    }

    private async Task<BrainRuntime> OpenAsync(string workspace, EvaluationContext context, List<string> extraSources, CancellationToken cancellationToken)
    {
        var projectRoot = Path.Combine(workspace, context.Name + (extraSources.Count > 0 ? "-" + string.Join('-', extraSources) : ""));
        var configDirectory = Directory.CreateDirectory(Path.Combine(projectRoot, ".agent-brain")).FullName;
        var json = new StringBuilder();
        json.Append("{ \"schema\": 1, \"project\": { \"id\": \"").Append(context.Project).Append("\", \"technologies\": [")
            .Append(string.Join(", ", context.Technologies.Select(t => $"\"{t}\""))).Append("], \"packages\": {")
            .Append(string.Join(", ", context.Packages.Select(p => $"\"{p.Key}\": \"{p.Value}\""))).Append("} }, \"collections\": {");
        json.Append(string.Join(", ", CorpusCollections.Select(c => $"\"{c}\": {{ \"path\": \"{Path.Combine(evalRoot, "corpus", c).Replace('\\', '/')}\" }}")));
        json.Append("} }");
        await File.WriteAllTextAsync(Path.Combine(configDirectory, "config.json"), json.ToString(), cancellationToken);

        var userDirectory = Directory.CreateDirectory(Path.Combine(workspace, "user")).FullName;
        var environment = new HostEnvironment(name => name == ConfigLocations.ConfigDirVariable ? userDirectory : null, OperatingSystem.IsWindows(), workspace);
        var additional = extraSources
            .Select(s => (s, (IReadOnlyList<string>)[.. Directory.EnumerateDirectories(Path.Combine(evalRoot, s)).Order(StringComparer.Ordinal)]))
            .ToList();
        var opened = await BrainRuntime.OpenAsync(new RuntimeOptions(projectRoot) { Environment = environment, Tokenizer = tokenizer, AdditionalSources = additional }, cancellationToken);
        return opened.IsSuccess ? opened.Value : throw new InvalidOperationException(opened.Error.ToString());
    }

    private static SearchInput Input(EvaluationCase testCase, Relaxation? relaxation, int? limit = null, int? maxBytes = null) => new(testCase.Query)
    {
        Files = testCase.Files,
        Symbols = testCase.Symbols,
        Kinds = testCase.Kinds?.Select(k => WireNames.Kinds.TryParse(k, out var kind) ? kind : throw new InvalidDataException(k)).ToList(),
        Include = new InclusionOptions(testCase.IncludeHistory, testCase.IncludeIncompatible),
        Relaxation = relaxation,
        Limit = limit,
        MaxBytes = maxBytes,
        MaxTokens = maxBytes is null ? null : maxBytes / 4,
    };

    private static async Task<VariantResult> RunVariantAsync(BrainRuntime runtime, EvaluationCase testCase, Variant variant, CancellationToken cancellationToken)
    {
        Relaxation? relaxation = null;
        if (variant.Relaxation is { } text && Relaxation.TryParse(text, out var value))
        {
            relaxation = value;
        }

        var stopwatch = Stopwatch.StartNew();
        var response = await runtime.Search.ExecuteAsync(Input(testCase, relaxation), TextRenderer.Compact, cancellationToken);
        stopwatch.Stop();
        if (!response.IsSuccess)
        {
            return new VariantResult(testCase.Id, variant.Relaxation ?? "default", testCase.Categories, false, [response.Error.ToString()], 0, 0, 0, 0, 0, 0, stopwatch.Elapsed.TotalMilliseconds, [], []);
        }

        var results = response.Value.Results.Select(r => r.Entry.Ref.ToString()).ToList();
        var blocking = response.Value.Blocking.Select(r => r.Entry.Ref.ToString()).ToList();
        var expect = variant.Expect;
        var failures = new List<string>();
        failures.AddRange(expect.Relevant.Where(r => !results.Contains(r)).Select(r => "missing " + r));
        failures.AddRange(expect.Irrelevant.Where(results.Contains).Select(r => "unexpected " + r));
        failures.AddRange(expect.Blocking.Where(r => !blocking.Contains(r)).Select(r => "missing blocking " + r));
        failures.AddRange(expect.NotBlocking.Where(blocking.Contains).Select(r => "unexpected blocking " + r));
        var conflicted = response.Value.Results.Where(r => r.Conflict is not null).Select(r => r.Entry.Ref.ToString()).ToHashSet(StringComparer.Ordinal);
        failures.AddRange(expect.Conflicts.Where(r => !conflicted.Contains(r)).Select(r => "missing conflict " + r));
        if (expect.First is { } expectedFirst && (results.Count == 0 || results[0] != expectedFirst))
        {
            failures.Add($"expected {expectedFirst} first, got {(results.Count == 0 ? "nothing" : results[0])}");
        }

        if (expect.MaxOccurrences is { } max)
        {
            failures.AddRange(results.GroupBy(r => r).Where(g => g.Count() > max).Select(g => $"{g.Key} appears {g.Count()} times"));
        }
        if (expect.Empty == true && results.Count + blocking.Count > 0)
        {
            failures.Add("expected no results, got " + string.Join(", ", results.Concat(blocking)));
        }

        return new VariantResult(
            testCase.Id,
            variant.Relaxation ?? "default",
            testCase.Categories,
            failures.Count == 0,
            failures,
            expect.Relevant.Count,
            expect.Relevant.Count(results.Contains),
            expect.Irrelevant.Count,
            expect.Irrelevant.Count(results.Contains),
            results.Count,
            response.Value.UsedBytes,
            stopwatch.Elapsed.TotalMilliseconds,
            results,
            blocking);
    }

    /// <summary>Everything admitted at strict must be admitted at broad (spec §7.4), measured without count or byte limits.</summary>
    private static async Task<MonotonicityViolation?> CheckMonotonicityAsync(BrainRuntime runtime, EvaluationCase testCase, CancellationToken cancellationToken)
    {
        var strict = await runtime.Search.ExecuteAsync(Input(testCase, Relaxation.Strict, 100, 1_000_000), TextRenderer.Compact, cancellationToken);
        var broad = await runtime.Search.ExecuteAsync(Input(testCase, Relaxation.Broad, 100, 1_000_000), TextRenderer.Compact, cancellationToken);
        if (!strict.IsSuccess || !broad.IsSuccess)
        {
            return null;
        }

        var broadRefs = broad.Value.Results.Select(r => r.Entry.Ref).ToHashSet();
        var lost = strict.Value.Results.Select(r => r.Entry.Ref).Where(r => !broadRefs.Contains(r)).Select(r => r.ToString()).ToList();
        return lost.Count == 0 ? null : new MonotonicityViolation(testCase.Id, lost);
    }
}
