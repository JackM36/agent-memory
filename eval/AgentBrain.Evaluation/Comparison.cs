using System.Globalization;
using AgentBrain.Application.Decisions;
using AgentBrain.Infrastructure.Sqlite;

namespace AgentBrain.Evaluation;

/// <summary>
/// <c>--compare heuristic,jev,hybrid</c> (ADR 0010): runs the same cases once per relevance implementation and prints
/// accuracy, latency and what the model cost side by side. A model-backed run needs the key in the environment;
/// without it that run is skipped and says why, so a table never shows a heuristic result under a model's name.
/// </summary>
internal static class Comparison
{
    public static async Task<int> RunAsync(string evalRoot, IndexTokenizer tokenizer, int phase, IReadOnlyList<string> modes)
    {
        var unknown = modes.Where(m => m is not ("heuristic" or "jev" or "hybrid")).ToList();
        if (unknown.Count > 0)
        {
            await Console.Error.WriteLineAsync($"--compare takes heuristic, jev and hybrid, not: {string.Join(", ", unknown)}");
            return 2;
        }

        var haveKey = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(DecisionDefaults.ApiKeyEnv));
        Console.WriteLine($"{"relevance",-10} {"passed",-14} {"recall",-12} {"irrelevant",-11} {"p50 ms",-8} {"p95 ms",-8} {"calls",-7} {"tokens",-9} {"est. $",-9}");
        var failed = false;
        foreach (var mode in modes.Distinct())
        {
            if (mode != "heuristic" && !haveKey)
            {
                Console.WriteLine($"{mode,-10} skipped: environment variable {DecisionDefaults.ApiKeyEnv} is not set (no model call was made)");
                continue;
            }

            var meter = new DecisionMeter();
            var (results, violations) = await new EvaluationRunner(evalRoot, tokenizer, phase, mode, meter).RunAsync(CancellationToken.None);
            var passed = results.Count(r => r.Passed);
            var expected = results.Sum(r => r.RelevantExpected);
            var found = results.Sum(r => r.RelevantFound);
            var listed = results.Sum(r => r.IrrelevantListed);
            var irrelevant = results.Sum(r => r.IrrelevantFound);
            var latencies = results.Select(r => r.Milliseconds).Order().ToList();
            double Percentile(double p) => latencies.Count == 0 ? 0 : latencies[Math.Min(latencies.Count - 1, (int)Math.Ceiling(p * latencies.Count) - 1)];
            var line = string.Create(
                CultureInfo.InvariantCulture,
                $"{mode,-10} {$"{passed}/{results.Count}",-14} {$"{found}/{expected}",-12} {$"{irrelevant}/{listed}",-11} {Percentile(0.5),-8:0.0} {Percentile(0.95),-8:0.0} {meter.Calls,-7} {meter.InputTokens,-9} {meter.EstimatedDollars,-9:0.0000}");
            Console.WriteLine(line + (violations.Count > 0 ? $"  monotonicity violations: {violations.Count}" : ""));
            failed |= mode == "heuristic" && violations.Count > 0;
        }

        Console.WriteLine("calls and tokens are what the model was sent (cache hits excluded); cost is estimated at the published input price.");
        return failed ? 1 : 0;
    }
}
