using AgentBrain.Application.Capture;
using AgentBrain.Domain.Entries;
using AgentBrain.Domain.Identity;

namespace AgentBrain.Application.Tests;

public sealed class SecretScannerTests
{
    private static EntryMetadata Meta(string summary = "Ordinary summary") =>
        new() { Id = EntryId.Parse("x"), Kind = EntryKind.Memory, Title = "T", Summary = summary };

    [Theory]
    [InlineData("private-key-block", "-----BEGIN RSA PRIVATE KEY-----\nabc")]
    [InlineData("aws-access-key", "key AKIAIOSFODNN7EXAMPLE here")]
    [InlineData("github-token", "token ghp_0123456789abcdefghijklmnopqrstuvwxyzAB")]
    [InlineData("slack-token", "xoxb-1234567890-abcdefghij")]
    [InlineData("anthropic-key", "sk-ant-api03-abcdefghijklmnopqrstuvwxyz")]
    [InlineData("openai-key", "sk-proj-abcdefghijklmnopqrstuvwxyz012345")]
    [InlineData("agent-brain-key", "brain_k1a2b3c4_abcdefghijklmnopqrstuvwxyz234567")]
    [InlineData("jwt", "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U")]
    [InlineData("connection-string-password", "Server=db;User=sa;Password=hunter22;")]
    [InlineData("generic-credential-assignment", "api_key = 9f8e7d6c5b4a3f2e1d0c")]
    public void Each_rule_detects_its_pattern_and_reports_the_line_not_the_value(string rule, string body)
    {
        var finding = Assert.Single(SecretScanner.Scan(Meta(), "line one\n" + body), f => f.Rule == rule);
        Assert.Equal("body", finding.Field);
        Assert.Equal(2, finding.Line);
    }

    [Fact]
    public void Ordinary_technical_text_is_not_flagged()
    {
        const string body = "Use the password field component. The api key is read from the AGENT_BRAIN_PERSONAL_KEY variable.\n" +
            "Tokens expire after an hour. sk-ant is a prefix. SerializeReference keeps types.";
        Assert.Empty(SecretScanner.Scan(Meta("Never store the token in PlayerPrefs"), body));
    }

    [Fact]
    public void Summaries_and_evidence_are_scanned_and_rules_can_be_allowed()
    {
        var metadata = Meta("key AKIAIOSFODNN7EXAMPLE") with { Evidence = ["password=hunter2222"] };
        Assert.Equal(["aws-access-key", "connection-string-password"], SecretScanner.Scan(metadata, "").Select(f => f.Rule).Order());
        Assert.Single(SecretScanner.Scan(metadata, "", ["aws-access-key"]));
    }
}
