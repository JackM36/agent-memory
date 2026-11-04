namespace AgentBrain.Application.Decisions;

/// <summary>
/// Counts what the decision model was asked and what it cost, for the evaluation's comparison and for
/// <c>brain decisions status</c>. Thread-safe; one per runtime.
/// </summary>
public sealed class DecisionMeter
{
    // $0.042 per million input tokens, output free (docs.typesafe.ai/models, read 2026-11-04). A reported estimate, not a bill.
    public const double DollarsPerInputToken = 0.042 / 1_000_000;

    private long _calls;
    private long _cacheHits;
    private long _failures;
    private long _inputTokens;

    public long Calls => Interlocked.Read(ref _calls);

    public long CacheHits => Interlocked.Read(ref _cacheHits);

    public long Failures => Interlocked.Read(ref _failures);

    public long InputTokens => Interlocked.Read(ref _inputTokens);

    public double EstimatedDollars => InputTokens * DollarsPerInputToken;

    /// <summary>A request that reached the model (or was answered by its cache) and what it cost.</summary>
    public void RecordCall(int inputTokens, bool fromCache)
    {
        if (fromCache)
        {
            Interlocked.Increment(ref _cacheHits);
            return;
        }

        Interlocked.Increment(ref _calls);
        Interlocked.Add(ref _inputTokens, inputTokens);
    }

    public void RecordFailure() => Interlocked.Increment(ref _failures);
}
