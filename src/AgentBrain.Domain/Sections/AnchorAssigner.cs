using System.Globalization;

namespace AgentBrain.Domain.Sections;

/// <summary>
/// Assigns unique anchors to the headings of one document in order: the first occurrence keeps the
/// base anchor, later duplicates get <c>-1</c>, <c>-2</c>, …. <see cref="HeadingAnchor.Intro"/> is reserved.
/// </summary>
public sealed class AnchorAssigner
{
    private readonly HashSet<string> _used = new(StringComparer.Ordinal) { HeadingAnchor.Intro };
    private readonly Dictionary<string, int> _nextSuffix = new(StringComparer.Ordinal);

    public string Assign(string headingText)
    {
        var baseAnchor = HeadingAnchor.FromHeadingText(headingText);
        if (_used.Add(baseAnchor))
        {
            return baseAnchor;
        }

        var n = _nextSuffix.GetValueOrDefault(baseAnchor, 1);
        string candidate;
        do
        {
            candidate = baseAnchor + "-" + n.ToString(CultureInfo.InvariantCulture);
            n++;
        }
        while (!_used.Add(candidate));

        _nextSuffix[baseAnchor] = n;
        return candidate;
    }
}
