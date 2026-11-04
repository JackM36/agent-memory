using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace AgentBrain.Domain.Identity;

/// <summary>
/// Stable collection identifier: lowercase alphanumeric segments separated by <c>.</c> or <c>-</c>,
/// at most 64 characters (e.g. <c>space-game.memory</c>). Validated, never silently normalised.
/// </summary>
public sealed partial record CollectionId
{
    public const int MaxLength = 64;

    private CollectionId(string value) => Value = value;

    public string Value { get; }

    public static bool IsValid([NotNullWhen(true)] string? value) =>
        value is { Length: > 0 and <= MaxLength } && Pattern().IsMatch(value);

    public static bool TryParse([NotNullWhen(true)] string? value, [NotNullWhen(true)] out CollectionId? id)
    {
        id = IsValid(value) ? new CollectionId(value) : null;
        return id is not null;
    }

    public static CollectionId Parse(string value) =>
        TryParse(value, out var id) ? id : throw new FormatException($"'{value}' is not a valid collection ID.");

    public int CompareTo(CollectionId? other) => string.CompareOrdinal(Value, other?.Value);

    public override string ToString() => Value;

    [GeneratedRegex("^[a-z0-9]+(?:[.-][a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
