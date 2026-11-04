namespace AgentBrain.Application.Common;

/// <summary>
/// A failed outcome: a code, a human message and structured details (e.g. the current revision on a
/// conflict, the minimum budget on a budget error). Details never contain credentials.
/// </summary>
public sealed record Error(ErrorCode Code, string Message, IReadOnlyDictionary<string, string>? Details = null)
{
    public static Error Validation(string message, IReadOnlyDictionary<string, string>? details = null) => new(ErrorCode.ValidationFailed, message, details);

    public static Error NotFound(string message) => new(ErrorCode.NotFound, message);

    public override string ToString() => $"{ErrorCodes.Names.Name(Code)}: {Message}";
}
