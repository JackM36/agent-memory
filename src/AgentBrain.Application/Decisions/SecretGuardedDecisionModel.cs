using AgentBrain.Application.Capture;
using AgentBrain.Application.Common;

namespace AgentBrain.Application.Decisions;

/// <summary>
/// Refuses to send a state that looks like it contains a secret (the same rules as <see cref="SecretScanner"/>),
/// so a token pasted into a query, title, summary or excerpt never reaches a third party. The refusal is an ordinary
/// unavailable result, so every port falls back to its heuristic. Sits innermost, next to the network.
/// </summary>
public sealed class SecretGuardedDecisionModel(IDecisionModel inner) : IDecisionModel
{
    public ValueTask<Result<DecisionResponse>> AskAsync(DecisionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (SecretScanner.ContainsSecret(request.State))
        {
            return ValueTask.FromResult<Result<DecisionResponse>>(
                new Error(ErrorCode.ProviderUnavailable, "decision model unavailable: the text looks like it contains a secret, so it was not sent"));
        }

        return inner.AskAsync(request, cancellationToken);
    }
}
