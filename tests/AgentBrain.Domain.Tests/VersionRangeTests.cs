using AgentBrain.Domain.Versions;

namespace AgentBrain.Domain.Tests;

public sealed class VersionRangeTests
{
    [Theory]
    [InlineData("1.2.3", new[] { 1, 2, 3 }, "")]
    [InlineData("2022.3.10f1", new[] { 2022, 3, 10 }, "f1")]
    [InlineData("1.0.0-preview.3", new[] { 1, 0, 0 }, "-preview.3")]
    [InlineData("6000.0", new[] { 6000, 0 }, "")]
    public void Versions_parse_into_core_and_suffix(string text, int[] core, string suffix)
    {
        Assert.True(VersionValue.TryParse(text, out var version));
        Assert.Equal(core, version.Core);
        Assert.Equal(suffix, version.Suffix);
    }

    [Theory]
    [InlineData("")]
    [InlineData("v1.2")]
    [InlineData("latest")]
    [InlineData("1.2 3")]
    [InlineData("1.2.3.4.5.6.7")]
    public void Unparseable_versions_are_rejected(string text) => Assert.False(VersionValue.TryParse(text, out _));

    [Theory]
    [InlineData(">=1.2 <2.0", "1.2", true)]
    [InlineData(">=1.2 <2.0", "1.9.9", true)]
    [InlineData(">=1.2 <2.0", "2.0", false)]
    [InlineData(">=1.2 <2.0", "1.1", false)]
    [InlineData(">= 1.2 < 2.0", "1.5", true)]
    [InlineData(">=1.0 <1.5 || >=3.1", "3.2", true)]
    [InlineData(">=1.0 <1.5 || >=3.1", "2.0", false)]
    [InlineData("=2022.3.10f1", "2022.3.10f1", true)]
    [InlineData("=2022.3.10f1", "2022.3.10f2", false)]
    [InlineData("2022.3.10", "2022.3.10", true)]
    [InlineData(">=2021.3", "2022.3.10f1", true)]
    [InlineData("<2022.3", "2022.3.0f1", false)]
    [InlineData(">1.0", "1.0.0", false)]
    [InlineData("<=1.0", "1", true)]
    public void Ranges_contain_versions(string range, string version, bool expected)
    {
        Assert.True(VersionRange.TryParse(range, out var parsed));
        Assert.True(VersionValue.TryParse(version, out var value));
        Assert.Equal(expected, parsed.Contains(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData(">>1.0")]
    [InlineData("<>1.0")]
    [InlineData(">=")]
    [InlineData("^1.2")]
    [InlineData("~1.2")]
    [InlineData(">=1.0 ||")]
    public void Malformed_ranges_are_rejected(string range) => Assert.False(VersionRange.TryParse(range, out _));

    [Theory]
    [InlineData(">=1.0", "1.5", VersionCompatibility.Compatible)]
    [InlineData(">=1.0", "0.9", VersionCompatibility.Incompatible)]
    [InlineData(">=1.0", null, VersionCompatibility.Unknown)]
    [InlineData(null, "1.0", VersionCompatibility.Unknown)]
    [InlineData(">=1.0", "latest", VersionCompatibility.Unknown)]
    [InlineData("whatever", "1.0", VersionCompatibility.Unknown)]
    public void Unknown_is_never_assumed_compatible(string? range, string? version, VersionCompatibility expected) =>
        Assert.Equal(expected, VersionRange.Check(range, version));
}
