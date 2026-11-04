using AgentBrain.Application.Abstractions.Support;
using AgentBrain.Application.Common;
using AgentBrain.Application.Configuration;
using AgentBrain.Application.Decisions;
using AgentBrain.Domain.Entries;
using AgentBrain.Domain.Identity;

namespace AgentBrain.Application.Operations.Decisions;

/// <summary>One port as configured: its mode and whether it will really use a model.</summary>
public sealed record PortStatus(string Port, string Mode, bool UsesModel);

/// <summary>What <c>brain decisions status</c> reports. Never contains the key, only whether it is set.</summary>
public sealed record DecisionStatus(
    IReadOnlyList<PortStatus> Ports,
    string Model,
    bool ModelIsAlias,
    string KeyVariable,
    bool KeySet,
    IReadOnlyList<string> AllowedCollections,
    IReadOnlyList<string> Problems);

/// <summary>
/// The decision ports on their own: classify an entry before saving it, ask the capture gate, and report the
/// configuration (ADR 0010). Everything here informs the caller; nothing writes.
/// </summary>
public sealed class DecisionsOperation(EffectiveConfiguration configuration, DecisionServices services, ICredentialResolver? credentials = null)
{
    public DecisionStatus Status()
    {
        var s = configuration.Decisions;
        var keySet = credentials?.Resolve(s.Jev.Credential) is { Length: > 0 };
        var problems = new List<string>(services.Problems);
        if (s.UsesModel && !keySet && !problems.Any(p => p.Contains(s.Jev.Credential.EnvironmentVariable, StringComparison.Ordinal)))
        {
            problems.Add($"A decision port uses jev but environment variable {s.Jev.Credential.EnvironmentVariable} is not set; heuristics are used instead.");
        }

        var allowed = configuration.Collections.Where(c => c.AllowExternalDecisions).Select(c => c.Id.Value).ToList();
        if (s.UsesModel && allowed.Count == 0)
        {
            problems.Add("No collection has allowExternalDecisions, so no entry text may be sent to the model and every port behaves as its heuristic.");
        }

        if (s.UsesModel && s.Jev.IsAlias)
        {
            problems.Add($"'{s.Jev.Model}' is an alias that moves to newer models; pin a version such as '{DecisionDefaults.Model}'.");
        }

        return new DecisionStatus(
            [
                new PortStatus("relevance", DecisionModes.Names.Name(s.Relevance), s.Relevance != DecisionMode.Heuristic),
                new PortStatus("classifier", DecisionModes.Names.Name(s.Classifier), s.Classifier != DecisionMode.Heuristic),
                new PortStatus("duplicates", DecisionModes.Names.Name(s.Duplicates), s.Duplicates != DecisionMode.Heuristic),
                new PortStatus("captureGate", s.CaptureGate ? "jev" : "off", s.CaptureGate),
            ],
            s.Jev.Model,
            s.Jev.IsAlias,
            s.Jev.Credential.EnvironmentVariable,
            keySet,
            allowed,
            problems);
    }

    /// <summary>The classifier's view of an entry; the suggestion is null when the classifier has none.</summary>
    public async ValueTask<Result<ClassificationResult>> ClassifyAsync(CollectionId? destination, EntryKind chosen, string title, string summary, IReadOnlyList<string> tags, string? excerpt, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(summary))
        {
            return Error.Validation("Give a title and a summary to classify.");
        }

        var target = destination ?? configuration.MemoryCollection;
        return await services.Classifier.ClassifyAsync(new ClassificationInput(target, chosen, title, summary, tags, excerpt), cancellationToken);
    }

    public async ValueTask<Result<CaptureDecision>> GateAsync(CollectionId? destination, string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Error.Validation("Give the text to evaluate.");
        }

        return await services.CaptureGate.EvaluateAsync(new CaptureCandidate(destination ?? configuration.MemoryCollection, text), cancellationToken);
    }
}
