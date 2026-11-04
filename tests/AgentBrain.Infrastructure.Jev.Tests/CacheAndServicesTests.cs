using AgentBrain.Application.Common;
using AgentBrain.Application.Decisions;
using AgentBrain.Domain.Identity;

namespace AgentBrain.Infrastructure.Jev.Tests;

public sealed class CacheAndServicesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "jev-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static DecisionRequest Request(string state = "SECRET-STATE-TEXT") =>
        new(state, new Dictionary<string, DecisionQuestion> { ["a"] = new NoulQuestion("Is it?") });

    [Fact]
    public async Task A_repeated_request_is_answered_from_the_cache_without_calling_the_model()
    {
        var inner = FakeModel.AlwaysNoul(0.7);
        var cache = new CachingDecisionModel(inner, "jev-1.14.0", null);

        var first = await cache.AskAsync(Request(), CancellationToken.None);
        var second = await cache.AskAsync(Request(), CancellationToken.None);

        Assert.Equal(1, inner.Calls);
        Assert.False(first.Value.FromCache);
        Assert.True(second.Value.FromCache);
        Assert.Equal(0, second.Value.InputTokens);
        Assert.Equal(0.7, second.Value.Noul("a"));
    }

    [Fact]
    public async Task A_different_state_or_model_is_a_different_key()
    {
        var inner = FakeModel.AlwaysNoul(0.7);
        var cache = new CachingDecisionModel(inner, "jev-1.14.0", null);
        await cache.AskAsync(Request("one"), CancellationToken.None);
        await cache.AskAsync(Request("two"), CancellationToken.None);
        Assert.Equal(2, inner.Calls);

        var other = new CachingDecisionModel(inner, "jev-1.13.0", null);
        await other.AskAsync(Request("one"), CancellationToken.None);
        Assert.Equal(3, inner.Calls);
    }

    [Fact]
    public async Task Failures_are_not_cached()
    {
        var fail = true;
        var inner = new FakeModel(_ => fail ? FakeModel.Failure() : FakeModel.Noul(("a", 0.4)));
        var cache = new CachingDecisionModel(inner, "m", null);

        Assert.False((await cache.AskAsync(Request(), CancellationToken.None)).IsSuccess);
        fail = false;
        var retry = await cache.AskAsync(Request(), CancellationToken.None);

        Assert.True(retry.IsSuccess);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task The_cache_file_reloads_and_never_stores_the_state_text()
    {
        var file = Path.Combine(_directory, "decisions", "cache.jsonl");
        var inner = FakeModel.AlwaysNoul(0.9);
        await new CachingDecisionModel(inner, "m", file).AskAsync(Request(), CancellationToken.None);

        var text = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("SECRET-STATE-TEXT", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Is it?", text, StringComparison.Ordinal);

        var second = FakeModel.AlwaysNoul(0.1);
        var reloaded = await new CachingDecisionModel(second, "m", file).AskAsync(Request(), CancellationToken.None);

        Assert.Equal(0, second.Calls);
        Assert.True(reloaded.Value.FromCache);
        Assert.Equal(0.9, reloaded.Value.Noul("a"));
    }

    [Fact]
    public async Task A_corrupt_cache_line_is_skipped()
    {
        var file = Path.Combine(_directory, "cache.jsonl");
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(file, "{torn\n\n", TestContext.Current.CancellationToken);
        var inner = FakeModel.AlwaysNoul(0.5);

        var result = await new CachingDecisionModel(inner, "m", file).AskAsync(Request(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public void Nothing_configured_returns_the_heuristics_without_problems()
    {
        var services = JevDecisionServices.Create(DecisionSettings.Off, null, ExternalDecisionPolicy.None, null);

        Assert.Same(HeuristicRelevanceJudge.Instance, services.Relevance);
        Assert.Same(HeuristicEntryClassifier.Instance, services.Classifier);
        Assert.Same(HeuristicDuplicateJudge.Instance, services.Duplicates);
        Assert.Same(HeuristicCaptureGate.Instance, services.CaptureGate);
        Assert.Empty(services.Problems);
    }

    [Fact]
    public void A_missing_key_falls_back_to_the_heuristics_and_names_the_variable()
    {
        var settings = Settings.Decisions(DecisionMode.Jev, DecisionMode.Jev, gate: true);

        var services = JevDecisionServices.Create(settings, "  ", ExternalDecisionPolicy.None, _directory);

        Assert.Same(HeuristicRelevanceJudge.Instance, services.Relevance);
        Assert.Same(HeuristicEntryClassifier.Instance, services.Classifier);
        Assert.Same(HeuristicCaptureGate.Instance, services.CaptureGate);
        var problem = Assert.Single(services.Problems);
        Assert.Contains("TYPESAFE_API_KEY", problem, StringComparison.Ordinal);
        Assert.Contains("decisions.relevance", problem, StringComparison.Ordinal);
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public void Only_the_ports_set_to_a_model_become_model_backed()
    {
        var settings = Settings.Decisions(DecisionMode.Hybrid, duplicates: DecisionMode.Jev);

        var services = JevDecisionServices.Create(settings, null, ExternalDecisionPolicy.None, null, modelOverride: FakeModel.AlwaysNoul(0.5));

        Assert.IsType<JevRelevanceJudge>(services.Relevance);
        Assert.IsType<JevDuplicateJudge>(services.Duplicates);
        Assert.Same(HeuristicEntryClassifier.Instance, services.Classifier);
        Assert.Same(HeuristicCaptureGate.Instance, services.CaptureGate);
        Assert.Empty(services.Problems);
    }

    [Fact]
    public async Task A_key_builds_a_working_http_model_over_the_given_handler()
    {
        var handler = new StubHandler((_, _, _) => Task.FromResult(StubHandler.Json("""{"answers":{"remember":{"type":"noul","noul":0.9}},"usage":{"input_tokens":5,"output_tokens":1}}""")));
        var services = JevDecisionServices.Create(Settings.Decisions(gate: true), Settings.ApiKey, new AllowOnly("mem"), _directory, handler);

        var decision = await services.CaptureGate.EvaluateAsync(new CaptureCandidate(CollectionId.Parse("mem"), "text"), CancellationToken.None);

        Assert.True(decision.Remember);
        Assert.Equal(0.9, decision.Likelihood);
        Assert.Equal(1, handler.Calls);
        Assert.True(File.Exists(Path.Combine(_directory, "decisions", "cache.jsonl")));
    }

    [Fact]
    public async Task The_meter_counts_calls_tokens_cache_hits_and_failures()
    {
        var meter = new DecisionMeter();
        var fail = false;
        var model = new FakeModel(_ => fail ? FakeModel.Failure() : FakeModel.Noul(("remember", 0.9)));
        var services = JevDecisionServices.Create(Settings.Decisions(gate: true), null, new AllowOnly("mem"), null, modelOverride: model, meter: meter);
        var candidate = new CaptureCandidate(CollectionId.Parse("mem"), "text");

        await services.CaptureGate.EvaluateAsync(candidate, CancellationToken.None);
        await services.CaptureGate.EvaluateAsync(candidate, CancellationToken.None);
        fail = true;
        await services.CaptureGate.EvaluateAsync(new CaptureCandidate(CollectionId.Parse("mem"), "other text"), CancellationToken.None);

        Assert.Equal(1, meter.Calls);
        Assert.Equal(1, meter.CacheHits);
        Assert.Equal(1, meter.Failures);
        Assert.Equal(10, meter.InputTokens);
        Assert.Equal(1, model.Calls - 1);
    }

    [Fact]
    public void Error_results_use_the_provider_unavailable_code()
    {
        Assert.Equal(ErrorCode.ProviderUnavailable, FakeModel.Failure().Error!.Code);
    }
}
