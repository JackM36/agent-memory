using AgentBrain.Application.Retrieval;

namespace AgentBrain.Application.Abstractions.Support;

/// <summary>One logged search: the query, its context and what came back. Local only; never sent anywhere.</summary>
public sealed record QueryLogRecord(DateTimeOffset At, string Query, QueryContext Context, IReadOnlyList<string> Results, IReadOnlyList<string> Blocking);

/// <summary>
/// Opt-in local query log (<c>search.log: true</c>) that supplies real cases for calibration and corpus
/// growth (plan §9.3). Failures to log never fail a search.
/// </summary>
public interface IQueryLog
{
    ValueTask AppendAsync(QueryLogRecord record, CancellationToken cancellationToken);
}
