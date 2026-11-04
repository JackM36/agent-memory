using System.Globalization;
using System.Text;
using AgentBrain.Application.Abstractions.Support;
using AgentBrain.Application.Common;

namespace AgentBrain.Application.Retrieval.Budgeting;

/// <summary>
/// Fits a search outcome into hard limits (spec §7.8, plan §6.1 stage 8). The complete rendered response
/// is measured in UTF-8 bytes, including headers and source notices. Blocking uncertainties are budgeted
/// first; results are added in rank order and the first one that does not fit ends the list, so a lower
/// ranked result never displaces a higher one and no summary is ever cut.
/// </summary>
public static class ResponseBudgeter
{
    /// <summary>Starting estimate of UTF-8 bytes per token; a calibration item (plan §5.9).</summary>
    public const double BytesPerToken = 4.0;

    public static int ByteLimit(int maxBytes, int? maxTokens) =>
        maxTokens is { } tokens ? Math.Min(maxBytes, (int)Math.Ceiling(tokens * BytesPerToken)) : maxBytes;

    public static Result<SearchResponse> Fit(SearchOutcome outcome, int maxResults, int byteLimit, IResponseRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(renderer);

        SearchResponse Build(int blockingCount, int resultCount) => new(
            outcome,
            [.. outcome.Results.Take(resultCount)],
            [.. outcome.Blocking.Take(blockingCount)],
            Math.Max(0, Math.Min(outcome.Results.Count, maxResults) - resultCount),
            outcome.Blocking.Count - blockingCount,
            byteLimit,
            0);

        int Measure(SearchResponse response) => Encoding.UTF8.GetByteCount(renderer.RenderSearch(Finalize(response, renderer)));

        var minimum = Measure(Build(0, 0));
        if (minimum > byteLimit)
        {
            return new Error(ErrorCode.BudgetTooSmall,
                $"A response needs at least {minimum} bytes; the budget allows {byteLimit}.",
                new Dictionary<string, string> { ["minimumBytes"] = minimum.ToString(CultureInfo.InvariantCulture) });
        }

        var blocking = 0;
        while (blocking < outcome.Blocking.Count && Measure(Build(blocking + 1, 0)) <= byteLimit)
        {
            blocking++;
        }

        var results = 0;
        var limit = Math.Min(outcome.Results.Count, maxResults);
        while (results < limit && Measure(Build(blocking, results + 1)) <= byteLimit)
        {
            results++;
        }

        return Finalize(Build(blocking, results), renderer);
    }

    /// <summary>Fills in the rendered size. Rendering again with the final number changes at most a few digits, kept within the limit by measuring twice.</summary>
    private static SearchResponse Finalize(SearchResponse response, IResponseRenderer renderer)
    {
        var used = Encoding.UTF8.GetByteCount(renderer.RenderSearch(response));
        var withSize = response with { UsedBytes = used };
        return withSize with { UsedBytes = Encoding.UTF8.GetByteCount(renderer.RenderSearch(withSize)) };
    }
}
