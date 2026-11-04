using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace AgentBrain.Domain.Sections;

/// <summary>
/// The one heading-anchor convention shared by link generation, validation and section retrieval
/// (plan §5.13). GitHub-style: lowercase, punctuation removed except <c>-</c> and <c>_</c>,
/// spaces become <c>-</c>. Unicode letters and digits are kept.
/// </summary>
public static class HeadingAnchor
{
    /// <summary>Anchor of the body text before the first heading.</summary>
    public const string Intro = "_intro";

    /// <summary>Used when a heading has no letters or digits at all.</summary>
    public const string Fallback = "section";

    public static string FromHeadingText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Trim())
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
            else if (c is ' ' or '-')
            {
                builder.Append('-');
            }
            else if (c == '_')
            {
                builder.Append('_');
            }
        }

        return builder.Length == 0 ? Fallback : builder.ToString();
    }

    /// <summary>True for any string <see cref="FromHeadingText"/> or <see cref="AnchorAssigner"/> can produce.</summary>
    public static bool IsValid([NotNullWhen(true)] string? anchor)
    {
        if (string.IsNullOrEmpty(anchor) || anchor.Length > 200)
        {
            return false;
        }

        foreach (var c in anchor)
        {
            var allowed = c is '-' or '_' || (char.IsLetterOrDigit(c) && !char.IsUpper(c));
            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }
}
