namespace AgentBrain.Domain.Matching;

public enum MatchStrength
{
    None,
    Weak,
    Strong,
}

/// <summary>Code symbol matching (plan §5.6): exact case-sensitive is strong, case-insensitive is weak.</summary>
public static class SymbolMatcher
{
    public static MatchStrength Match(string entrySymbol, string requestSymbol)
    {
        ArgumentNullException.ThrowIfNull(entrySymbol);
        ArgumentNullException.ThrowIfNull(requestSymbol);
        if (string.Equals(entrySymbol, requestSymbol, StringComparison.Ordinal))
        {
            return MatchStrength.Strong;
        }

        return string.Equals(entrySymbol, requestSymbol, StringComparison.OrdinalIgnoreCase) ? MatchStrength.Weak : MatchStrength.None;
    }
}
