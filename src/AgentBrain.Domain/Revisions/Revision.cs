using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace AgentBrain.Domain.Revisions;

/// <summary>
/// Opaque content revision (plan §5.11): <c>r1-</c> followed by 26 lowercase base32 characters of a
/// SHA-256 over the canonical content. Equal content has an equal revision in every adapter and copy.
/// </summary>
public sealed partial record Revision
{
    public const string Prefix = "r1-";
    public const int HashLength = 26;

    private Revision(string value) => Value = value;

    public string Value { get; }

    public static bool TryParse([NotNullWhen(true)] string? text, [NotNullWhen(true)] out Revision? revision)
    {
        revision = text is not null && Pattern().IsMatch(text) ? new Revision(text) : null;
        return revision is not null;
    }

    public static Revision Parse(string text) =>
        TryParse(text, out var revision) ? revision : throw new FormatException($"'{text}' is not a revision.");

    internal static Revision FromHash(string base32) => new(Prefix + base32[..HashLength]);

    public override string ToString() => Value;

    [GeneratedRegex("^r1-[a-z2-7]{26}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
