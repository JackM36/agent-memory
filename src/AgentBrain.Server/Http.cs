using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AgentBrain.Application.Authorization;
using AgentBrain.Application.Common;
using AgentBrain.Composition;
using AgentBrain.Contracts.V1;

namespace AgentBrain.Server;

/// <summary>HTTP plumbing: authentication, the per-request caller, JSON in and out, and error-to-status mapping.</summary>
internal static class Http
{
    private const string CallerKey = "agent-brain.caller";
    private const string KeyKey = "agent-brain.key";

    /// <summary>
    /// Authenticates <c>/v1</c> requests with a bearer API key and serialises them through the database gate
    /// (SQLite connections are single-threaded). <c>/health</c> needs no key.
    /// </summary>
    public static async Task AuthenticateAsync(HttpContext context, RequestDelegate next)
    {
        if (!context.Request.Path.StartsWithSegments("/v1", StringComparison.Ordinal))
        {
            await next(context);
            return;
        }

        if (context.Request.Headers.TryGetValue(ProtocolVersions.Header, out var protocol)
            && protocol.ToString() != ProtocolVersions.Protocol.ToString(CultureInfo.InvariantCulture))
        {
            await WriteErrorAsync(context, new Error(ErrorCode.ProtocolVersionUnsupported, $"This server speaks protocol {ProtocolVersions.Protocol}; the client sent {protocol}."));
            return;
        }

        var runtime = context.RequestServices.GetRequiredService<HostRuntime>();
        var header = context.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header["Bearer ".Length..].Trim() : null;

        await runtime.Database.Gate.WaitAsync(context.RequestAborted);
        try
        {
            var key = await runtime.Keys.AuthenticateAsync(token, context.RequestAborted);
            if (key is null)
            {
                context.Response.Headers.WWWAuthenticate = "Bearer";
                await WriteErrorAsync(context, new Error(ErrorCode.Unauthorized, "A valid API key is required (Authorization: Bearer brain_…)."));
                return;
            }

            context.Items[KeyKey] = key;
            context.Items[CallerKey] = new CallerContext(key.Label, "http:" + key.KeyId, ApiKeyService.Grants(key.Scopes));
            await next(context);
        }
        finally
        {
            runtime.Database.Gate.Release();
        }
    }

    public static CallerContext Caller(HttpContext context) => (CallerContext)context.Items[CallerKey]!;

    public static ApiKeyRecord Key(HttpContext context) => (ApiKeyRecord)context.Items[KeyKey]!;

    public static async Task<Result<T>> ReadAsync<T>(HttpContext context, JsonTypeInfo<T> type)
    {
        try
        {
            var value = await JsonSerializer.DeserializeAsync(context.Request.Body, type, context.RequestAborted);
            return value is null ? Error.Validation("The request body is empty.") : value;
        }
        catch (JsonException ex)
        {
            return Error.Validation($"The request body is not valid: {ex.Message}");
        }
    }

    public static Task WriteJsonAsync(HttpContext context, string json, int status = StatusCodes.Status200OK)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        return context.Response.WriteAsync(json, context.RequestAborted);
    }

    public static Task WriteJsonAsync<T>(HttpContext context, T value, JsonTypeInfo<T> type, int status = StatusCodes.Status200OK) =>
        WriteJsonAsync(context, JsonSerializer.Serialize(value, type), status);

    public static Task WriteErrorAsync(HttpContext context, Error error) =>
        WriteJsonAsync(context, DtoMapper.ToDto(error), ContractsJsonContext.Default.ErrorDto, Status(error.Code));

    public static int Status(ErrorCode code) => code switch
    {
        ErrorCode.ValidationFailed or ErrorCode.ConfigInvalid or ErrorCode.ProtocolVersionUnsupported or ErrorCode.BudgetTooSmall => StatusCodes.Status400BadRequest,
        ErrorCode.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorCode.Forbidden or ErrorCode.ReadOnly or ErrorCode.UnsupportedOperation => StatusCodes.Status403Forbidden,
        ErrorCode.NotFound or ErrorCode.SectionNotFound => StatusCodes.Status404NotFound,
        ErrorCode.RevisionConflict or ErrorCode.LockTimeout => StatusCodes.Status409Conflict,
        ErrorCode.SecretDetected => StatusCodes.Status422UnprocessableEntity,
        ErrorCode.ProviderUnavailable or ErrorCode.IndexUnavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError,
    };
}
