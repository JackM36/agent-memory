using System.Diagnostics.CodeAnalysis;

namespace AgentBrain.Application.Common;

/// <summary>Success with a value, or an <see cref="Common.Error"/>. Operations return results instead of throwing.</summary>
public readonly struct Result<T>
{
    private readonly T? _value;

    private Result(T? value, Error? error)
    {
        _value = value;
        Error = error;
    }

    public Error? Error { get; }

    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Error is null;

    /// <summary>The value; throws when the result is a failure (a bug at the call site).</summary>
    public T Value => IsSuccess ? _value! : throw new InvalidOperationException($"Result is a failure: {Error}");

    public static Result<T> Success(T value) => new(value, null);

    public static Result<T> Failure(Error error) => new(default, error ?? throw new ArgumentNullException(nameof(error)));

#pragma warning disable CA2225 // Implicit conversions are the intended way to return results.
    public static implicit operator Result<T>(T value) => Success(value);

    public static implicit operator Result<T>(Error error) => Failure(error);
#pragma warning restore CA2225

    public bool TryGetValue([NotNullWhen(true)] out T? value)
    {
        value = IsSuccess ? _value : default;
        return IsSuccess && value is not null;
    }
}

/// <summary>Success without a value, or an <see cref="Common.Error"/>.</summary>
public readonly struct Result
{
    private Result(Error? error) => Error = error;

    public static Result Success { get; } = new(null);

    public Error? Error { get; }

    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Error is null;

    public static Result Failure(Error error) => new(error ?? throw new ArgumentNullException(nameof(error)));

#pragma warning disable CA2225 // Implicit conversion is the intended way to return failures.
    public static implicit operator Result(Error error) => Failure(error);
#pragma warning restore CA2225
}
