using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AgentBrain.Application.Common;
using AgentBrain.Application.Decisions;

namespace AgentBrain.Infrastructure.Jev;

/// <summary>
/// The Jev System One endpoint as an <see cref="IDecisionModel"/>. One call is one HTTP request, limited to
/// <see cref="JevSettings.MaxConcurrency"/> in flight. <see cref="JevSettings.Timeout"/> is the total deadline for a call
/// including retries of 429 and 529 and of transient network errors, counted from when the call gets a slot.
/// Every failure is a <see cref="ErrorCode.ProviderUnavailable"/> result; only the caller's own cancellation throws.
/// The key and the state text never appear in a message.
/// </summary>
public sealed class JevDecisionModel : IDecisionModel, IDisposable
{
    public static readonly Uri DefaultBaseAddress = new("https://api.typesafe.ai");

    private const int MaxAttempts = 4;

    private readonly HttpClient _http;
    private readonly JevSettings _settings;
    private readonly string _apiKey;
    private readonly SemaphoreSlim _slots;

    public JevDecisionModel(HttpMessageHandler? handler, string apiKey, JevSettings settings, Uri? baseAddress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentNullException.ThrowIfNull(settings);
        _apiKey = apiKey;
        _settings = settings;
        _slots = new SemaphoreSlim(Math.Max(1, settings.MaxConcurrency));
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.BaseAddress = baseAddress ?? DefaultBaseAddress;
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    /// <summary>First retry delay, doubled each attempt. A <c>Retry-After</c> header takes precedence. Tests shorten it.</summary>
    internal TimeSpan InitialBackoff { get; init; } = TimeSpan.FromMilliseconds(100);

    public async ValueTask<Result<DecisionResponse>> AskAsync(DecisionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = DecisionWire.WriteRequest(request, _settings.Model);
        await _slots.WaitAsync(cancellationToken);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_settings.Timeout);
            return await SendWithRetriesAsync(request, body, deadline.Token, cancellationToken);
        }
        finally
        {
            _slots.Release();
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _slots.Dispose();
    }

    private async Task<Result<DecisionResponse>> SendWithRetriesAsync(DecisionRequest request, string body, CancellationToken deadline, CancellationToken caller)
    {
        var delay = InitialBackoff;
        for (var attempt = 1; ; attempt++)
        {
            string? retryReason;
            TimeSpan? retryAfter = null;
            try
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, "/v1/systemone")
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseContentRead, deadline);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync(deadline);
                    return DecisionWire.ReadResponse(json, request) is { } parsed
                        ? parsed
                        : Unavailable("the response was not in the expected format");
                }

                switch (response.StatusCode)
                {
                    case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                        return Unavailable($"TypeSafe rejected the API key held in {_settings.Credential.EnvironmentVariable} (HTTP {(int)response.StatusCode})");
                    case HttpStatusCode.TooManyRequests or (HttpStatusCode)529:
                        retryReason = $"HTTP {(int)response.StatusCode}";
                        retryAfter = RetryAfter(response);
                        break;
                    default:
                        return Unavailable($"TypeSafe returned HTTP {(int)response.StatusCode}");
                }
            }
            catch (HttpRequestException ex)
            {
                retryReason = $"network error ({ex.GetType().Name})";
            }
            catch (OperationCanceledException) when (caller.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return Unavailable($"timed out after {_settings.Timeout.TotalMilliseconds:0} ms");
            }

            if (attempt >= MaxAttempts)
            {
                return Unavailable($"{retryReason} after {attempt} attempts");
            }

            var wait = retryAfter ?? delay;
            delay *= 2;
            try
            {
                await Task.Delay(wait, deadline);
            }
            catch (OperationCanceledException) when (caller.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return Unavailable($"{retryReason}; timed out after {_settings.Timeout.TotalMilliseconds:0} ms while waiting to retry");
            }
        }
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (header?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }

        return null;
    }

    private static Error Unavailable(string detail) => new(ErrorCode.ProviderUnavailable, "decision model unavailable: " + detail);
}
