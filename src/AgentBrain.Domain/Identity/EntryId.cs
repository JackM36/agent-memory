using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentBrain.Domain.Identity;

/// <summary>
/// Stable entry identifier, unique within its collection: lowercase alphanumeric words joined by
/// <c>-</c>, at most 80 characters. Independent of file name and path.
/// </summary>
public sealed partial record EntryId
{
    public const int MaxLength = 80;

    private EntryId(string value) => Value = value;

    public string Value { get; }

    public static bool IsValid([NotNullWhen(true)] string? value) =>
        value is { Length: > 0 and <= MaxLength } && Pattern().IsMatch(value);

    public static bool TryParse([NotNullWhen(true)] string? value, [NotNullWhen(true)] out EntryId? id)
    {
        id = IsValid(value) ? new EntryId(value) : null;
        return id is not null;
    }

    public static EntryId Parse(string value) =>
        TryParse(value, out var id) ? id : throw new FormatException($"'{value}' is not a valid entry ID.");

    /// <summary>Generates an ID from a title (see <see cref="Slug"/>).</summary>
    public static EntryId FromTitle(string title) => new(Slug.Create(title, MaxLength));

    /// <summary>Returns the collision variant <c>id-n</c> (n ≥ 2), shortening the base to stay within the length limit.</summary>
    public EntryId WithCollisionSuffix(int n)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(n, 2);
        var suffix = "-" + n.ToString(CultureInfo.InvariantCulture);
        var stem = Value.Length + suffix.Length <= MaxLength ? Value : Value[..(MaxLength - suffix.Length)].TrimEnd('-');
        return new EntryId(stem + suffix);
    }

    public int CompareTo(EntryId? other) => string.CompareOrdinal(Value, other?.Value);

    public override string ToString() => Value;

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
