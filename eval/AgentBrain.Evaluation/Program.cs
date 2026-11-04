using System.Globalization;
using System.Text;
using System.Text.Json;
using AgentBrain.Evaluation;
using AgentBrain.Infrastructure.Sqlite;

// brain-eval [--tokenizer porter|unicode61] [--phase N] [--out path] [--verbose] [--relevance heuristic|jev|hybrid] [--compare heuristic,jev,hybrid]
// Runs the evaluation cases (eval/cases) against the corpus (eval/corpus) and prints metrics.
Console.OutputEncoding = new UTF8Encoding(false);
if (args is ["scan", .. var sizes])
{
    return await ScanBenchmark.RunAsync(sizes.Length > 0 ? [.. sizes.Select(s => int.Parse(s, CultureInfo.InvariantCulture))] : [100, 1000, 10000]);
}

var tokenizer = IndexTokenizer.Porter;
var phase = 2;
string? output = null;
var verbose = false;
double? minPass = null;
int? maxIrrelevant = null;
var relevance = "heuristic";
string[]? compare = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--tokenizer":
            tokenizer = Enum.Parse<IndexTokenizer>(args[++i], ignoreCase: true);
            break;
        case "--phase":
            phase = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--out":
            output = args[++i];
            break;
        case "--verbose":
            verbose = true;
            break;
        case "--min-pass":
            minPass = double.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--relevance":
            relevance = args[++i].ToLowerInvariant();
            break;
        case "--compare":
            compare = [.. args[++i].ToLowerInvariant().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
            break;
        case "--max-irrelevant":
            maxIrrelevant = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        default:
            await Console.Error.WriteLineAsync($"unknown argument {args[i]}");
            return 2;
    }
}

var evalRoot = FindEvalRoot();
if (compare is not null)
{
    return await Comparison.RunAsync(evalRoot, tokenizer, phase, compare);
}

if (relevance is not ("heuristic" or "jev" or "hybrid"))
{
    await Console.Error.WriteLineAsync("--relevance must be heuristic, jev or hybrid");
    return 2;
}

var (results, violations) = await new EvaluationRunner(evalRoot, tokenizer, phase, relevance).RunAsync(CancellationToken.None);

var passed = results.Count(r => r.Passed);
var relevantExpected = results.Sum(r => r.RelevantExpected);
var relevantFound = results.Sum(r => r.RelevantFound);
var irrelevantListed = results.Sum(r => r.IrrelevantListed);
var irrelevantFound = results.Sum(r => r.IrrelevantFound);
var latencies = results.Select(r => r.Milliseconds).Order().ToList();
double Percentile(double p) => latencies.Count == 0 ? 0 : latencies[Math.Min(latencies.Count - 1, (int)Math.Ceiling(p * latencies.Count) - 1)];

Console.WriteLine($"tokenizer {tokenizer} · {results.Count} case variants · {passed} passed ({100.0 * passed / Math.Max(1, results.Count):0.0}%)");
Console.WriteLine($"recall of expected entries: {relevantFound}/{relevantExpected} ({100.0 * relevantFound / Math.Max(1, relevantExpected):0.0}%)");
Console.WriteLine($"irrelevant entries returned: {irrelevantFound}/{irrelevantListed} listed as irrelevant ({100.0 * irrelevantFound / Math.Max(1, irrelevantListed):0.0}%)");
Console.WriteLine($"response bytes: mean {results.Average(r => r.Bytes):0} · max {results.Max(r => r.Bytes)} · est. tokens mean ~{results.Average(r => r.Bytes) / 4:0}");
Console.WriteLine($"results per query: mean {results.Average(r => r.ResultCount):0.0}");
Console.WriteLine($"latency (search incl. freshness check): p50 {Percentile(0.5):0.0} ms · p95 {Percentile(0.95):0.0} ms");
Console.WriteLine($"relaxation monotonicity violations: {violations.Count}");
foreach (var v in violations)
{
    Console.WriteLine($"  {v.CaseId}: lost at broad {string.Join(", ", v.LostAtBroad)}");
}

foreach (var category in results.SelectMany(r => r.Categories).Distinct().Order(StringComparer.Ordinal))
{
    var inCategory = results.Where(r => r.Categories.Contains(category)).ToList();
    Console.WriteLine($"  {category,-28} {inCategory.Count(r => r.Passed)}/{inCategory.Count}");
}

foreach (var r in results.Where(r => !r.Passed || verbose))
{
    Console.WriteLine($"{(r.Passed ? "PASS" : "FAIL")} {r.CaseId} [{r.Variant}] {string.Join("; ", r.Failures)}");
    if (verbose || !r.Passed)
    {
        Console.WriteLine($"     results: {string.Join(", ", r.Results)}{(r.Blocking.Count > 0 ? " | blocking: " + string.Join(", ", r.Blocking) : "")}");
    }
}

if (output is not null)
{
    using var stream = File.Create(output);
    using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
    writer.WriteStartObject();
    writer.WriteString("tokenizer", tokenizer.ToString());
    writer.WriteNumber("variants", results.Count);
    writer.WriteNumber("passed", passed);
    writer.WriteNumber("relevantExpected", relevantExpected);
    writer.WriteNumber("relevantFound", relevantFound);
    writer.WriteNumber("irrelevantListed", irrelevantListed);
    writer.WriteNumber("irrelevantFound", irrelevantFound);
    writer.WriteNumber("meanBytes", Math.Round(results.Average(r => r.Bytes)));
    writer.WriteNumber("maxBytes", results.Max(r => r.Bytes));
    writer.WriteNumber("monotonicityViolations", violations.Count);
    writer.WriteStartArray("cases");
    foreach (var r in results)
    {
        writer.WriteStartObject();
        writer.WriteString("id", r.CaseId);
        writer.WriteString("variant", r.Variant);
        writer.WriteBoolean("passed", r.Passed);
        writer.WriteStartArray("failures");
        foreach (var failure in r.Failures)
        {
            writer.WriteStringValue(failure);
        }

        writer.WriteEndArray();
        writer.WriteNumber("bytes", r.Bytes);
        writer.WriteEndObject();
    }

    writer.WriteEndArray();
    writer.WriteEndObject();
}

// Regression gate (plan §9.3): fail when quality drops below the accepted baseline.
var passRate = (double)passed / Math.Max(1, results.Count);
if ((minPass is { } min && passRate < min) || (maxIrrelevant is { } max && irrelevantFound > max) || violations.Count > 0)
{
    await Console.Error.WriteLineAsync($"evaluation gate failed: pass rate {passRate:0.000} (min {minPass}), irrelevant {irrelevantFound} (max {maxIrrelevant}), monotonicity violations {violations.Count}");
    return 1;
}

return 0;

static string FindEvalRoot()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
    {
        var candidate = Path.Combine(dir.FullName, "eval");
        if (Directory.Exists(Path.Combine(candidate, "corpus")))
        {
            return candidate;
        }
    }

    throw new InvalidOperationException("eval/corpus not found.");
}
