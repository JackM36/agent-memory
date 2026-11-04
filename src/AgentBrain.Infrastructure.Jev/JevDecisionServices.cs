using AgentBrain.Application.Decisions;

namespace AgentBrain.Infrastructure.Jev;

/// <summary>Builds the decision ports the configuration asks for. Ports left on the heuristic stay the built-in implementations.</summary>
public static class JevDecisionServices
{
    public static DecisionServices Create(
        DecisionSettings settings,
        string? apiKey,
        IExternalDecisionPolicy policy,
        string? stateDirectory,
        HttpMessageHandler? handler = null,
        IDecisionModel? modelOverride = null,
        DecisionMeter? meter = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(policy);
        if (!settings.UsesModel)
        {
            return Heuristics(settings, []);
        }

        IDecisionModel inner;
        if (modelOverride is not null)
        {
            inner = modelOverride;
        }
        else if (string.IsNullOrWhiteSpace(apiKey))
        {
            var ports = new List<string>();
            if (settings.Relevance != DecisionMode.Heuristic)
            {
                ports.Add("decisions.relevance");
            }

            if (settings.Classifier != DecisionMode.Heuristic)
            {
                ports.Add("decisions.classifier");
            }

            if (settings.Duplicates != DecisionMode.Heuristic)
            {
                ports.Add("decisions.duplicates");
            }

            if (settings.CaptureGate)
            {
                ports.Add("decisions.captureGate");
            }

            return Heuristics(settings, [$"{string.Join(", ", ports)} use the decision model but environment variable {settings.Jev.Credential.EnvironmentVariable} is not set; using heuristics."]);
        }
        else
        {
            inner = new JevDecisionModel(handler, apiKey, settings.Jev);
        }

        var cacheFile = stateDirectory is null ? null : Path.Combine(stateDirectory, "decisions", "cache.jsonl");
        IDecisionModel model = new CachingDecisionModel(new SecretGuardedDecisionModel(inner), settings.Jev.Model, cacheFile);
        if (meter is not null)
        {
            model = new MeteredDecisionModel(model, meter);
        }

        return new DecisionServices(
            settings.Relevance == DecisionMode.Heuristic ? HeuristicRelevanceJudge.Instance : new JevRelevanceJudge(model, settings, policy),
            settings.Classifier == DecisionMode.Heuristic ? HeuristicEntryClassifier.Instance : new JevEntryClassifier(model, policy),
            settings.Duplicates == DecisionMode.Heuristic ? HeuristicDuplicateJudge.Instance : new JevDuplicateJudge(model, settings, policy),
            settings.CaptureGate ? new JevCaptureGate(model, policy) : HeuristicCaptureGate.Instance,
            settings,
            []);
    }

    private static DecisionServices Heuristics(DecisionSettings settings, IReadOnlyList<string> problems) => new(
        HeuristicRelevanceJudge.Instance, HeuristicEntryClassifier.Instance, HeuristicDuplicateJudge.Instance, HeuristicCaptureGate.Instance, settings, problems);
}
