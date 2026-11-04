using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace AgentBrain.Domain.Versions;

/// <summary>
/// Version range (plan §5.5): comparators separated by spaces are AND (<c>&gt;=1.2 &lt;2.0</c>),
/// <c>||</c> separates alternatives, <c>=</c> (or a bare version) means exact.
/// </summary>
public sealed partial class VersionRange
{
    private readonly IReadOnlyList<IReadOnlyList<Comparator>> _alternatives;

    private VersionRange(string text, IReadOnlyList<IReadOnlyList<Comparator>> alternatives)
    {
        Text = text;
        _alternatives = alternatives;
    }

    public string Text { get; }

    public static bool TryParse([NotNullWhen(true)] string? text, [NotNullWhen(true)] out VersionRange? range)
    {
        range = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var alternatives = new List<IReadOnlyList<Comparator>>();
        foreach (var alternative in text.Split("||"))
        {
            var normalized = OperatorSpacing().Replace(alternative.Trim(), "$1");
            if (normalized.Length == 0)
            {
                return false;
            }

            var comparators = new List<Comparator>();
            foreach (var token in normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!TryParseComparator(token, out var comparator))
                {
                    return false;
                }

                comparators.Add(comparator);
            }

            alternatives.Add(comparators);
        }

        range = new VersionRange(text.Trim(), alternatives);
        return true;
    }

    public bool Contains(VersionValue version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return _alternatives.Any(all => all.All(c => c.IsSatisfiedBy(version)));
    }

    /// <summary>
    /// Compatibility of a declared range with a concrete context version. Unknown whenever either
    /// side is missing or unparseable — never assumed compatible.
    /// </summary>
    public static VersionCompatibility Check(string? declaredRange, string? contextVersion)
    {
        if (!TryParse(declaredRange, out var range) || !VersionValue.TryParse(contextVersion, out var version))
        {
            return VersionCompatibility.Unknown;
        }

        return range.Contains(version) ? VersionCompatibility.Compatible : VersionCompatibility.Incompatible;
    }

    public override string ToString() => Text;

    private static bool TryParseComparator(string token, out Comparator comparator)
    {
        comparator = default;
        var versionText = token.TrimStart('>', '<', '=');
        Operator? op = token[..(token.Length - versionText.Length)] switch
        {
            "" or "=" => Operator.Exact,
            ">" => Operator.Greater,
            ">=" => Operator.GreaterOrEqual,
            "<" => Operator.Less,
            "<=" => Operator.LessOrEqual,
            _ => null,
        };
        if (op is null || !VersionValue.TryParse(versionText, out var version))
        {
            return false;
        }

        comparator = new Comparator(op.Value, version);
        return true;
    }

    [GeneratedRegex(@"(>=|<=|>|<|=)\s+", RegexOptions.CultureInvariant)]
    private static partial Regex OperatorSpacing();

    private enum Operator
    {
        Exact,
        Greater,
        GreaterOrEqual,
        Less,
        LessOrEqual,
    }

    private readonly record struct Comparator(Operator Op, VersionValue Version)
    {
        public bool IsSatisfiedBy(VersionValue candidate) => Op switch
        {
            Operator.Exact => candidate.ExactlyEquals(Version),
            Operator.Greater => candidate.CompareTo(Version) > 0,
            Operator.GreaterOrEqual => candidate.CompareTo(Version) >= 0,
            Operator.Less => candidate.CompareTo(Version) < 0,
            Operator.LessOrEqual => candidate.CompareTo(Version) <= 0,
            _ => false,
        };
    }
}
