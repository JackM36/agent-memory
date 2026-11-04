using AgentBrain.Application.Common;
using AgentBrain.Application.Decisions;

namespace AgentBrain.Infrastructure.Jev;

/// <summary>The outermost wrapper: records every answered and failed request, cache hits as cache hits, in a <see cref="DecisionMeter"/>.</summary>
internal sealed class MeteredDecisionModel(IDecisionModel inner, DecisionMeter meter) : IDecisionModel
{
    public async ValueTask<Result<DecisionResponse>> AskAsync(DecisionRequest request, CancellationToken cancellationToken)
    {
        var result = await inner.AskAsync(request, cancellationToken);
        if (result.IsSuccess)
        {
            meter.RecordCall(result.Value.InputTokens, result.Value.FromCache);
        }
        else
        {
            meter.RecordFailure();
        }

        return result;
    }
}
