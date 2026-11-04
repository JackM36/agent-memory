using System.Text;
using AgentBrain.Application.Abstractions.Support;
using AgentBrain.Application.Common;
using AgentBrain.Application.Configuration;
using AgentBrain.Application.Retrieval;
using AgentBrain.Domain.Identity;

namespace AgentBrain.Application.Operations.Content;

/// <summary>Results of a (batch) get: what was fetched and, separately, what failed.</summary>
public sealed record GetResult(IReadOnlyList<EntryContent> Entries, IReadOnlyList<GetFailure> Failures);

/// <summary>
/// Full-entry, section and bounded batch retrieval (spec §7.7). Each target gets at most the per-entry
/// limit and the batch shares one total limit; truncation is always explicit.
/// </summary>
public sealed class GetOperation(EffectiveConfiguration configuration, RetrievalCoordinator coordinator, IndexFreshness freshness)
{
    public async ValueTask<Result<GetResult>> ExecuteAsync(IReadOnlyList<EntryRef> targets, int? maxBytes, bool withReferences, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var settings = configuration.Get;
        if (targets.Count == 0)
        {
            return Error.Validation("Give at least one entry reference.");
        }

        if (targets.Count > settings.BatchTargets)
        {
            return Error.Validation($"A batch get is limited to {settings.BatchTargets} targets.");
        }

        await freshness.EnsureFreshAsync(cancellationToken);
        var perEntry = Math.Min(maxBytes ?? settings.MaxBytes, settings.MaxBytes);
        var remaining = settings.BatchBytes;
        var entries = new List<EntryContent>();
        var failures = new List<GetFailure>();

        foreach (var target in targets)
        {
            if (remaining <= 0)
            {
                failures.Add(new GetFailure(target, new Error(ErrorCode.BudgetTooSmall, "The batch byte limit was reached; fetch this target separately.")));
                continue;
            }

            var providers = coordinator.ProvidersFor(target.Collection);
            if (providers.Count == 0)
            {
                failures.Add(new GetFailure(target, Error.NotFound($"Collection '{target.Collection}' is not configured.")));
                continue;
            }

            // The preferred source first (spec §8.4); fall back to the next only when a source is unavailable.
            Result<EntryContent> result = default;
            foreach (var provider in providers)
            {
                result = await provider.GetAsync(new GetRequest(target, Math.Min(perEntry, remaining), withReferences), cancellationToken);
                // A source that serves "whatever its key reads" may simply not have it: try the next one.
                var notHere = result.Error?.Code == ErrorCode.NotFound && provider.Collections.Count == 0;
                if (result.IsSuccess || (!notHere && result.Error!.Code is not (ErrorCode.ProviderUnavailable or ErrorCode.Unauthorized)))
                {
                    break;
                }
            }

            if (result.IsSuccess)
            {
                entries.Add(result.Value);
                remaining -= Encoding.UTF8.GetByteCount(result.Value.Body);
            }
            else
            {
                failures.Add(new GetFailure(target, result.Error));
            }
        }

        return new GetResult(entries, failures);
    }
}
