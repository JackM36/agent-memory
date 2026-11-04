using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace AgentBrain.Domain.Versions;

/// <summary>
/// A version with a dotted numeric core and an optional suffix: <c>1.2.3</c>, <c>2022.3.10f1</c>,
/// <c>1.0.0-preview.3</c>. Ordering uses only the zero-padded numeric core (plan §5.5);
/// the suffix matters only for exact (<c>=</c>) matches.
/// </summary>
public sealed class VersionValue
{
    private const int MaxSegments = 6;

    private readonly int[] _core;

    private VersionValue(int[] core, string suffix, string text)
    {
        _core = core;
        Suffix = suffix;
        Text = text;
    }

    public string Suffix { get; }

    public string Text { get; }

    public IReadOnlyList<int> Core => _core;

    public static bool TryParse([NotNullWhen(true)] string? text, [NotNullWhen(true)] out VersionValue? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        text = text.Trim();
        var segments = new List<int>(4);
        var position = 0;
        while (true)
        {
            var start = position;
            while (position < text.Length && char.IsAsciiDigit(text[position]))
            {
                position++;
            }

            if (position == start
                || !int.TryParse(text.AsSpan(start, position - start), NumberStyles.None, CultureInfo.InvariantCulture, out var segment)
                || segments.Count == MaxSegments)
            {
                return false;
            }

            segments.Add(segment);
            if (position < text.Length && text[position] == '.' && position + 1 < text.Length && char.IsAsciiDigit(text[position + 1]))
            {
                position++;
                continue;
            }

            break;
        }

        var suffix = text[position..];
        if (suffix.Any(char.IsWhiteSpace))
        {
            return false;
        }

        version = new VersionValue([.. segments], suffix, text);
        return true;
    }

    public int CompareTo(VersionValue? other)
    {
        if (other is null)
        {
            return 1;
        }

        var length = Math.Max(_core.Length, other._core.Length);
        for (var i = 0; i < length; i++)
        {
            var a = i < _core.Length ? _core[i] : 0;
            var b = i < other._core.Length ? other._core[i] : 0;
            if (a != b)
            {
                return a.CompareTo(b);
            }
        }

        return 0;
    }

    /// <summary>Exact match: equal numeric core and identical suffix (case-insensitive).</summary>
    public bool ExactlyEquals(VersionValue other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return CompareTo(other) == 0 && string.Equals(Suffix, other.Suffix, StringComparison.OrdinalIgnoreCase);
    }

    public override string ToString() => Text;
}
