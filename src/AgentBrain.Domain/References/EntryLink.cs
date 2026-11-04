using System.Diagnostics.CodeAnalysis;
using AgentBrain.Domain.Identity;
using AgentBrain.Domain.Sections;

namespace AgentBrain.Domain.References;

/// <summary>
/// Canonical entry link: <c>entry://collection-id/entry-id[#anchor]</c> (spec §6.1). The URI carries
/// identity and an optional section only — never a host, secret or filesystem path. Non-ASCII
/// anchors are percent-encoded in the URI.
/// </summary>
public static class EntryLink
{
    public const string Scheme = "entry://";

    public static string Format(EntryRef reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var link = Scheme + reference.Collection + "/" + reference.Entry;
        return reference.Anchor is null ? link : link + "#" + Uri.EscapeDataString(reference.Anchor);
    }

    /// <summary>True when the text uses the entry scheme at all (valid or not), so callers can report malformed links.</summary>
    public static bool HasScheme(string? text) => text is not null && text.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);

    public static bool TryParse([NotNullWhen(true)] string? text, [NotNullWhen(true)] out EntryRef? reference)
    {
        reference = null;
        if (text is null || !text.StartsWith(Scheme, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = text[Scheme.Length..];
        if (rest.Contains('?', StringComparison.Ordinal))
        {
            return false;
        }

        var hash = rest.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0)
        {
            string anchor;
            try
            {
                anchor = Uri.UnescapeDataString(rest[(hash + 1)..]);
            }
            catch (UriFormatException)
            {
                return false;
            }

            if (!HeadingAnchor.IsValid(anchor))
            {
                return false;
            }

            rest = rest[..hash] + "#" + anchor;
        }

        return EntryRef.TryParse(rest, out reference);
    }
}
