using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentBrain.Domain.Matching;

/// <summary>
/// File association glob, relative to the project root (plan §5.6). <c>**</c> crosses directories,
/// <c>*</c> stays within a segment, <c>?</c> matches one character, a trailing <c>/</c> means
/// "everything below". A pattern without <c>/</c> matches a file name at any depth. Matching is
/// case-insensitive on every platform; character classes are not supported and match literally.
/// </summary>
public sealed class GlobPattern
{
    private readonly Regex _regex;

    private GlobPattern(string text, Regex regex)
    {
        Text = text;
        _regex = regex;
    }

    public string Text { get; }

    public static bool TryParse([NotNullWhen(true)] string? text, [NotNullWhen(true)] out GlobPattern? pattern)
    {
        pattern = null;
        if (string.IsNullOrWhiteSpace(text) || text.Contains('\\', StringComparison.Ordinal) || text.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        var normalized = text.Trim();
        var anchored = normalized.StartsWith('/');
        normalized = normalized.TrimStart('/');
        if (normalized.EndsWith('/'))
        {
            normalized += "**";
        }

        var segments = normalized.Split('/');
        if (normalized.Length == 0 || segments.Any(s => s.Length == 0 || s is "." or ".."))
        {
            return false;
        }

        var matchAnyDepth = !anchored && segments.Length == 1;
        var body = Translate(segments);
        var regex = new Regex(
            "^" + (matchAnyDepth ? "(?:.*/)?" : "") + body + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
        pattern = new GlobPattern(text.Trim(), regex);
        return true;
    }

    /// <summary>Matches a project-relative path. Backslashes and a leading <c>./</c> or <c>/</c> are normalised.</summary>
    public bool IsMatch(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        var path = relativePath.Replace('\\', '/');
        while (path.StartsWith("./", StringComparison.Ordinal))
        {
            path = path[2..];
        }

        return _regex.IsMatch(path.TrimStart('/'));
    }

    public override string ToString() => Text;

    private static string Translate(string[] segments)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            var last = i == segments.Length - 1;
            if (segment == "**")
            {
                // "**/" matches zero or more directories; a trailing "**" matches everything below.
                builder.Append(last ? ".*" : "(?:.*/)?");
                continue;
            }

            foreach (var c in segment)
            {
                builder.Append(c switch
                {
                    '*' => "[^/]*",
                    '?' => "[^/]",
                    _ => Regex.Escape(c.ToString()),
                });
            }

            if (!last)
            {
                builder.Append('/');
            }
        }

        return builder.ToString();
    }
}
