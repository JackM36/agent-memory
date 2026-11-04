using System.Net;
using AgentBrain.Application.Common;
using AgentBrain.Application.Decisions;

namespace AgentBrain.Infrastructure.Jev.Tests;

public sealed class ModelTests
{
    private const string NoulOk = """{"model":"jev-1.14.0","answers":{"a":{"type":"noul","noul":0.75}},"usage":{"input_tokens":42,"output_tokens":3}}""";

    private static DecisionRequest OneNoul() => new("the state", new Dictionary<string, DecisionQuestion> { ["a"] = new NoulQuestion("Is it?") });

    private static JevDecisionModel Model(StubHandler handler, JevSettings? settings = null) =>
        new(handler, Settings.ApiKey, settings ?? Settings.Jev()) { InitialBackoff = TimeSpan.FromMilliseconds(5) };

    [Fact]
    public async Task Sends_the_documented_request_with_a_bearer_key()
    {
        var handler = new StubHandler((_, _, _) => Task.FromResult(StubHandler.Json(NoulOk)));
        using var model = Model(handler);
        var request = new DecisionRequest("Ticket text", new Dictionary<string, DecisionQuestion>
        {
            ["is_urgent"] = new NoulQuestion("The message conveys urgency"),
            ["department"] = new ChoiceQuestion("Which team", [new("billing", "Payments"), new("technical", "Bugs")]),
            ["frustration"] = new ScoreQuestion("How frustrated", ["Calm", "Angry"]),
        });
        var wire = """{"model":"jev-1.14.0","answers":{"is_urgent":{"type":"noul","noul":0.99},"department":{"type":"choice","choice":"technical","confidence":1.0,"probabilities":{"technical":1.0,"billing":0.0}},"frustration":{"type":"score","score":0.67,"confidence":0.5,"probabilities":{"0":0.33,"1":0.67}}},"usage":{"input_tokens":428,"output_tokens":73}}""";
        var answering = new StubHandler((_, _, _) => Task.FromResult(StubHandler.Json(wire)));
        using var asked = Model(answering);

        var result = await asked.AskAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var (message, body) = Assert.Single(answering.Requests);
        Assert.Equal("POST", message.Method.Method);
        Assert.Equal("https://api.typesafe.ai/v1/systemone", message.RequestUri!.ToString());
        Assert.Equal("Bearer", message.Headers.Authorization!.Scheme);
        Assert.Equal(Settings.ApiKey, message.Headers.Authorization.Parameter);
        Assert.Equal(
            """{"model":"jev-1.14.0","state":"Ticket text","questions":{"department":{"type":"choice","instructions":"Which team","criteria":{"billing":"Payments","technical":"Bugs"}},"frustration":{"type":"score","instructions":"How frustrated","criteria":["Calm","Angry"]},"is_urgent":{"type":"noul","instructions":"The message conveys urgency"}}}""",
            body);
        Assert.Equal(0.99, ((NoulAnswer)result.Value.Answers["is_urgent"]).Value);
        var choice = Assert.IsType<ChoiceAnswer>(result.Value.Answers["department"]);
        Assert.Equal("technical", choice.Choice);
        Assert.Equal(1.0, choice.Confidence);
        Assert.Equal(0.67, ((ScoreAnswer)result.Value.Answers["frustration"]).Score);
        Assert.Equal(428, result.Value.InputTokens);
        _ = handler;
        _ = model;
    }

    [Fact]
    public async Task Retries_a_429_and_then_succeeds()
    {
        var attempt = 0;
        var handler = new StubHandler((_, _, _) =>
        {
            var response = Interlocked.Increment(ref attempt) == 1 ? StubHandler.Json("{}", HttpStatusCode.TooManyRequests) : StubHandler.Json(NoulOk);
            if (attempt == 1)
            {
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
            }

            return Task.FromResult(response);
        });
        using var model = Model(handler);

        var result = await model.AskAsync(OneNoul(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(0.75, result.Value.Noul("a"));
    }

    [Fact]
    public async Task Retries_529_and_network_errors_then_gives_up_after_four_attempts()
    {
        var handler = new StubHandler((_, _, _) => throw new HttpRequestException("connection reset"));
        using var model = Model(handler);

        var result = await model.AskAsync(OneNoul(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCode.ProviderUnavailable, result.Error.Code);
        Assert.Equal(4, handler.Calls);

        var overloaded = new StubHandler((_, _, _) => Task.FromResult(StubHandler.Json("{}", (HttpStatusCode)529)));
        using var other = Model(overloaded);
        Assert.False((await other.AskAsync(OneNoul(), CancellationToken.None)).IsSuccess);
        Assert.Equal(4, overloaded.Calls);
    }

    [Fact]
    public async Task A_rejected_key_is_an_unavailable_result_that_never_contains_the_key_or_the_state()
    {
        var handler = new StubHandler((_, _, _) => Task.FromResult(StubHandler.Json("""{"detail":"bad key the state"}""", HttpStatusCode.Unauthorized)));
        using var model = Model(handler);

        var result = await model.AskAsync(OneNoul(), CancellationToken.None);

        Assert.Equal(ErrorCode.ProviderUnavailable, result.Error!.Code);
        Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain(Settings.ApiKey, result.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("the state", result.Error.Message, StringComparison.Ordinal);
        Assert.Contains("TYPESAFE_API_KEY", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Other_http_errors_are_not_retried()
    {
        var handler = new StubHandler((_, _, _) => Task.FromResult(StubHandler.Json("""{"detail":"secret state echo"}""", HttpStatusCode.UnprocessableEntity)));
        using var model = Model(handler);

        var result = await model.AskAsync(OneNoul(), CancellationToken.None);

        Assert.Equal(ErrorCode.ProviderUnavailable, result.Error!.Code);
        Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain("secret state echo", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_timeout_is_a_total_deadline_and_an_unavailable_result()
    {
        var handler = new StubHandler(async (_, _, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return StubHandler.Json(NoulOk);
        });
        using var model = Model(handler, Settings.Jev(timeoutMs: 150));

        var result = await model.AskAsync(OneNoul(), CancellationToken.None);

        Assert.Equal(ErrorCode.ProviderUnavailable, result.Error!.Code);
        Assert.Contains("timed out", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_retry_delay_longer_than_the_deadline_gives_up_instead_of_waiting()
    {
        var handler = new StubHandler((_, _, _) =>
        {
            var response = StubHandler.Json("{}", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return Task.FromResult(response);
        });
        using var model = Model(handler, Settings.Jev(timeoutMs: 200));
        var started = DateTime.UtcNow;

        var result = await model.AskAsync(OneNoul(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"answers":{"a":{"type":"mystery"}}}""")]
    [InlineData("""{"answers":{}}""")]
    [InlineData("""{"model":"x"}""")]
    public async Task A_response_that_is_not_the_documented_shape_is_unavailable(string body)
    {
        var handler = new StubHandler((_, _, _) => Task.FromResult(StubHandler.Json(body)));
        using var model = Model(handler);

        var result = await model.AskAsync(OneNoul(), CancellationToken.None);

        Assert.Equal(ErrorCode.ProviderUnavailable, result.Error!.Code);
        Assert.DoesNotContain(Settings.ApiKey, result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Calls_in_flight_never_exceed_the_configured_concurrency()
    {
        var handler = new StubHandler(async (_, _, token) =>
        {
            await Task.Delay(40, token);
            return StubHandler.Json(NoulOk);
        });
        using var model = Model(handler, Settings.Jev(concurrency: 2, timeoutMs: 5000));

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => model.AskAsync(OneNoul(), CancellationToken.None).AsTask()));

        Assert.All(results, r => Assert.True(r.IsSuccess));
        Assert.Equal(8, handler.Calls);
        Assert.True(handler.MaxInFlight <= 2, $"saw {handler.MaxInFlight} in flight");
    }

    [Fact]
    public async Task The_callers_own_cancellation_is_not_swallowed()
    {
        var handler = new StubHandler(async (_, _, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return StubHandler.Json(NoulOk);
        });
        using var model = Model(handler, Settings.Jev(timeoutMs: 30_000));
        using var cancel = new CancellationTokenSource(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await model.AskAsync(OneNoul(), cancel.Token));
    }

    [Fact]
    public async Task The_request_body_never_contains_the_key()
    {
        var handler = new StubHandler((_, _, _) => Task.FromResult(StubHandler.Json(NoulOk)));
        using var model = Model(handler);

        await model.AskAsync(OneNoul(), CancellationToken.None);

        Assert.DoesNotContain(Settings.ApiKey, handler.Requests[0].Body, StringComparison.Ordinal);
    }
}
