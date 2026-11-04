using System.Text;

namespace AgentBrain.Domain.Identity;

/// <summary>
/// Turns free text into an identifier-safe slug: Latin diacritics folded to ASCII
/// (<see cref="LatinFolding"/>), lowercase, every run of other characters collapsed to a single <c>-</c>.
/// </summary>
public static class Slug
{
    public const string Fallback = "entry";

    /// <summary>Slug of <paramref name="text"/>, or <see cref="Fallback"/> when it has no usable letters or digits.</summary>
    public static string Create(string text, int maxLength) => TryCreate(text, maxLength) ?? Fallback;

    /// <summary>Slug of <paramref name="text"/>, or null when it has no usable letters or digits.</summary>
    public static string? TryCreate(string text, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 1);

        var builder = new StringBuilder(text.Length);
        var pendingSeparator = false;

        void Append(char c)
        {
            if (pendingSeparator && builder.Length > 0)
            {
                builder.Append('-');
            }

            builder.Append(c);
            pendingSeparator = false;
        }

        foreach (var c in text)
        {
            var lower = char.ToLowerInvariant(c);
            if (lower is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                Append(lower);
            }
            else if (LatinFolding.Fold(c) is { } folded)
            {
                foreach (var f in folded)
                {
                    Append(f);
                }
            }
            else
            {
                pendingSeparator = true;
            }
        }

        var slug = builder.Length <= maxLength ? builder.ToString() : builder.ToString(0, maxLength).TrimEnd('-');
        return slug.Length == 0 ? null : slug;
    }
}
