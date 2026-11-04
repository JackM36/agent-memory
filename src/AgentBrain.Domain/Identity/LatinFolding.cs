using System.Collections.Frozen;

namespace AgentBrain.Domain.Identity;

/// <summary>
/// Folds Latin letters with diacritics to ASCII (<c>ü</c>→<c>u</c>, <c>ß</c>→<c>ss</c>, <c>æ</c>→<c>ae</c>).
/// The binaries run with invariant globalization, where Unicode normalisation does not decompose
/// characters, so slugs use this explicit table instead. It gives identical results on every platform.
/// </summary>
internal static class LatinFolding
{
    private static readonly FrozenDictionary<char, string> Table = Build();

    public static string? Fold(char c) => Table.GetValueOrDefault(c);

    private static FrozenDictionary<char, string> Build()
    {
        // Each group: the ASCII result followed by the characters that fold to it.
        string[] groups =
        [
            "a àáâãäåāăą", "c çćĉċč", "d ďđð", "e èéêëēĕėęě", "g ĝğġģ", "h ĥħ", "i ìíîïĩīĭįı", "j ĵ",
            "k ķ", "l ĺļľŀł", "n ñńņňŉ", "o òóôõöøōŏő", "r ŕŗř", "s śŝşšſ", "t ţťŧ", "u ùúûüũūŭůűų",
            "w ŵ", "y ýÿŷ", "z źżž", "ss ß", "ae æ", "oe œ", "th þ", "ij ĳ",
        ];
        var table = new Dictionary<char, string>();
        foreach (var group in groups)
        {
            var space = group.IndexOf(' ', StringComparison.Ordinal);
            var ascii = group[..space];
            foreach (var c in group[(space + 1)..])
            {
                table[c] = ascii;
                table[char.ToUpperInvariant(c)] = ascii;
            }
        }

        return table.ToFrozenDictionary();
    }
}
