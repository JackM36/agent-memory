using System.Text.RegularExpressions;
using AgentBrain.Domain.Entries;

namespace AgentBrain.Application.Capture;

/// <summary>A suspected secret: which rule matched, in which field and line. Never the matched value.</summary>
public sealed record SecretFinding(string Rule, string Field, int Line);

/// <summary>
/// Rule-based secret detection for everything written or published (A7, plan §8.3). Findings block the
/// write; a single rule can be allowed for one write when the user confirms a false positive.
/// </summary>
public static partial class SecretScanner
{
    private static readonly (string Rule, Regex Pattern)[] Rules =
    [
        ("private-key-block", PrivateKey()),
        ("aws-access-key", AwsKey()),
        ("github-token", GitHubToken()),
        ("slack-token", SlackToken()),
        ("anthropic-key", AnthropicKey()),
        ("openai-key", OpenAiKey()),
        ("agent-brain-key", BrainKey()),
        ("jwt", Jwt()),
        ("connection-string-password", ConnectionStringPassword()),
        ("generic-credential-assignment", CredentialAssignment()),
    ];

    public static IReadOnlyList<string> RuleIds { get; } = [.. Rules.Select(r => r.Rule)];

    /// <summary>True when free text matches any secret rule.</summary>
    public static bool ContainsSecret(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var findings = new List<SecretFinding>();
        Check(findings, "text", text, null);
        return findings.Count > 0;
    }

    public static IReadOnlyList<SecretFinding> Scan(EntryMetadata metadata, string body, IReadOnlyCollection<string>? allowedRules = null)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(body);
        var findings = new List<SecretFinding>();
        Check(findings, "title", metadata.Title, allowedRules);
        Check(findings, "summary", metadata.Summary, allowedRules);
        for (var i = 0; i < metadata.Evidence.Count; i++)
        {
            Check(findings, $"evidence[{i}]", metadata.Evidence[i], allowedRules);
        }

        Check(findings, "body", body, allowedRules);
        return findings;
    }

    private static void Check(List<SecretFinding> findings, string field, string text, IReadOnlyCollection<string>? allowed)
    {
        foreach (var (rule, pattern) in Rules)
        {
            if (allowed?.Contains(rule) == true)
            {
                continue;
            }

            foreach (Match match in pattern.Matches(text))
            {
                var line = text.AsSpan(0, match.Index).Count('\n') + 1;
                findings.Add(new SecretFinding(rule, field, line));
                break;
            }
        }
    }

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKey();

    [GeneratedRegex(@"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b", RegexOptions.CultureInvariant)]
    private static partial Regex AwsKey();

    [GeneratedRegex(@"\b(?:gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{22,})\b", RegexOptions.CultureInvariant)]
    private static partial Regex GitHubToken();

    [GeneratedRegex(@"\bxox[abprs]-[A-Za-z0-9-]{10,}", RegexOptions.CultureInvariant)]
    private static partial Regex SlackToken();

    [GeneratedRegex(@"\bsk-ant-[A-Za-z0-9_-]{20,}", RegexOptions.CultureInvariant)]
    private static partial Regex AnthropicKey();

    [GeneratedRegex(@"\bsk-(?!ant-)(?:proj-)?[A-Za-z0-9_-]{20,}", RegexOptions.CultureInvariant)]
    private static partial Regex OpenAiKey();

    [GeneratedRegex(@"\bbrain_[a-z0-9]{6,}_[a-z2-7]{20,}", RegexOptions.CultureInvariant)]
    private static partial Regex BrainKey();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}", RegexOptions.CultureInvariant)]
    private static partial Regex Jwt();

    [GeneratedRegex(@"(?i)\b(?:password|pwd)\s*=\s*[^;\s""']{4,}", RegexOptions.CultureInvariant)]
    private static partial Regex ConnectionStringPassword();

    [GeneratedRegex(@"(?i)\b(?:password|passwd|secret|api[_-]?key|access[_-]?token|auth[_-]?token)\b\s*[:=]\s*[""']?[A-Za-z0-9_\-+/=.]{12,}", RegexOptions.CultureInvariant)]
    private static partial Regex CredentialAssignment();
}
