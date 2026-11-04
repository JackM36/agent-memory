using System.Diagnostics.CodeAnalysis;

namespace AgentBrain.Domain.Identity;

/// <summary>
/// Logical identity of an entry (<c>collection-id/entry-id</c>), optionally narrowed to a section
/// (<c>collection-id/entry-id#anchor</c>).
/// </summary>
public sealed record EntryRef(CollectionId Collection, EntryId Entry, string? Anchor = null)
{
    /// <summary>The same entry without a section anchor.</summary>
    public EntryRef WithoutAnchor() => Anchor is null ? this : this with { Anchor = null };

    public static bool TryParse([NotNullWhen(true)] string? text, [NotNullWhen(true)] out EntryRef? reference)
    {
        reference = null;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        string? anchor = null;
        var hash = text.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0)
        {
            anchor = text[(hash + 1)..];
            text = text[..hash];
            if (!Sections.HeadingAnchor.IsValid(anchor))
            {
                return false;
            }
        }

        var slash = text.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || text.IndexOf('/', slash + 1) >= 0)
        {
            return false;
        }

        if (!CollectionId.TryParse(text[..slash], out var collection) || !EntryId.TryParse(text[(slash + 1)..], out var entry))
        {
            return false;
        }

        reference = new EntryRef(collection, entry, anchor);
        return true;
    }

    public static EntryRef Parse(string text) =>
        TryParse(text, out var reference) ? reference : throw new FormatException($"'{text}' is not a valid entry reference (expected collection-id/entry-id[#anchor]).");

    public int CompareTo(EntryRef? other)
    {
        if (other is null)
        {
            return 1;
        }

        var result = Collection.CompareTo(other.Collection);
        if (result == 0)
        {
            result = Entry.CompareTo(other.Entry);
        }

        return result != 0 ? result : string.CompareOrdinal(Anchor, other.Anchor);
    }

    public override string ToString() => Anchor is null ? $"{Collection}/{Entry}" : $"{Collection}/{Entry}#{Anchor}";
}
