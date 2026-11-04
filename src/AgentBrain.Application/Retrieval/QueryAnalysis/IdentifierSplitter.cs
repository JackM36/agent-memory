using System.Text;

namespace AgentBrain.Application.Retrieval.QueryAnalysis;

/// <summary>
/// Splits code identifiers into words (plan §5.7): <c>SaveSystemV2</c> → <c>save system v2</c>,
/// <c>snake_case_name</c> → <c>snake case name</c>, <c>SaveSystem.WriteSlot</c> → <c>save system write slot</c>.
/// Used identically at index time and query time.
/// </summary>
public static class IdentifierSplitter
{
    /// <summary>Lowercase words of an identifier; a single-word identifier returns one word.</summary>
    public static IReadOnlyList<string> Split(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        var words = new List<string>();
        var current = new StringBuilder();

        void Flush()
        {
            if (current.Length > 0)
            {
                words.Add(current.ToString().ToLowerInvariant());
                current.Clear();
            }
        }

        for (var i = 0; i < identifier.Length; i++)
        {
            var c = identifier[i];
            if (!char.IsLetterOrDigit(c))
            {
                Flush();
                continue;
            }

            if (current.Length > 0)
            {
                var previous = identifier[i - 1];
                var next = i + 1 < identifier.Length ? identifier[i + 1] : '\0';
                var lowerToUpper = char.IsUpper(c) && (char.IsLower(previous) || char.IsDigit(previous));
                var acronymEnd = char.IsUpper(c) && char.IsUpper(previous) && char.IsLower(next);
                var letterToDigit = char.IsDigit(c) && char.IsLetter(previous) && !char.IsUpper(previous);
                if (lowerToUpper || acronymEnd || letterToDigit)
                {
                    Flush();
                }
            }

            current.Append(c);
        }

        Flush();
        return words;
    }

    /// <summary>True when the token looks like code: mixed case inside a word, or joined by <c>_</c> or <c>.</c>.</summary>
    public static bool IsIdentifier(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return Split(token).Count > 1;
    }
}
