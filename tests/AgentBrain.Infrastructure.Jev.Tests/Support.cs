using System.Net;
using AgentBrain.Application.Abstractions.Indexing;
using AgentBrain.Application.Abstractions.Support;
using AgentBrain.Application.Common;
using AgentBrain.Application.Decisions;
using AgentBrain.Domain.Entries;
using AgentBrain.Domain.Identity;
using AgentBrain.Domain.Revisions;

namespace AgentBrain.Infrastructure.Jev.Tests;

internal static class Settings
{
    public const string ApiKey = "tsk-test-secret-key-123";

    public static JevSettings Jev(int timeoutMs = 2000, int concurrency = 12, int calls = 150, int pool = 100, double minConfidence = 0.6) =>
        new(new CredentialReference("TYPESAFE_API_KEY"), "jev-1.14.0", TimeSpan.FromMilliseconds(timeoutMs), concurrency, calls, minConfidence, pool);

    public static DecisionSettings Decisions(
        DecisionMode relevance = DecisionMode.Heuristic,
        DecisionMode classifier = DecisionMode.Heuristic,
        DecisionMode duplicates = DecisionMode.Heuristic,
        bool gate = false,
        JevSettings? jev = null) =>
        new(relevance, classifier, duplicates, gate, jev ?? Jev());
}

/// <summary>An <see cref="HttpMessageHandler"/> that answers from a delegate and records what it was sent.</summary>
internal sealed class StubHandler(Func<HttpRequestMessage, string, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    private int _calls;
    private int _inFlight;
    private int _maxInFlight;

    public int Calls => _calls;

    public int MaxInFlight => _maxInFlight;

    public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (Requests)
        {
            Requests.Add((request, body));
        }

        Interlocked.Increment(ref _calls);
        var now = Interlocked.Increment(ref _inFlight);
        int seen;
        while (now > (seen = _maxInFlight))
        {
            Interlocked.CompareExchange(ref _maxInFlight, now, seen);
        }

        try
        {
            return await respond(request, body, cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }
}

/// <summary>A decision model that answers from a delegate and counts calls.</summary>
internal sealed class FakeModel(Func<DecisionRequest, Result<DecisionResponse>> answer) : IDecisionModel
{
    private int _calls;

    public int Calls => _calls;

    public List<DecisionRequest> Requests { get; } = [];

    public ValueTask<Result<DecisionResponse>> AskAsync(DecisionRequest request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        lock (Requests)
        {
            Requests.Add(request);
        }

        return ValueTask.FromResult(answer(request));
    }

    public static Result<DecisionResponse> Noul(params (string Name, double Value)[] answers) =>
        new DecisionResponse(answers.ToDictionary(a => a.Name, a => (DecisionAnswer)new NoulAnswer(a.Value), StringComparer.Ordinal), 10);

    public static Result<DecisionResponse> Failure(string message = "decision model unavailable: timed out after 2000 ms") =>
        new Error(ErrorCode.ProviderUnavailable, message);

    public static FakeModel AlwaysNoul(double value) => new(r => new DecisionResponse(
        r.Questions.ToDictionary(q => q.Key, _ => (DecisionAnswer)new NoulAnswer(value), StringComparer.Ordinal), 10));
}

internal sealed class AllowOnly(params string[] collections) : IExternalDecisionPolicy
{
    public bool Allows(CollectionId collection) => collections.Contains(collection.Value);
}

internal static class Candidates
{
    private static readonly Revision Rev = Revision.Parse("r1-" + new string('a', 26));

    /// <summary>A general-scope candidate. With <paramref name="hits"/> true the heuristic admits it; without, it does not.</summary>
    public static IndexCandidate Make(
        string reference,
        string title,
        bool hits = true,
        EntryKind kind = EntryKind.Knowledge,
        bool blocking = false,
        ApplicabilityScope scope = ApplicabilityScope.General,
        string[]? projects = null,
        int rank = 1)
    {
        var entryRef = EntryRef.Parse(reference);
        var metadata = new EntryMetadata
        {
            Id = entryRef.Entry,
            Kind = kind,
            Title = title,
            Summary = "Summary of " + title,
            Tags = ["tag-a"],
            Resolution = kind == EntryKind.Uncertainty ? Resolution.Unresolved : null,
            Blocking = blocking ? true : null,
        };
        return new IndexCandidate(
            new IndexedEntry(entryRef, metadata, new Applicability(scope, projects ?? [], [], [], []), Rev, 100, "x.md"),
            hits ? rank : null,
            hits ? [new ConceptHit(0, true), new ConceptHit(1, true)] : [],
            false);
    }

    public static CandidateSet Set(params IndexCandidate[] candidates) => new(candidates, [3, 3], 50);
}
