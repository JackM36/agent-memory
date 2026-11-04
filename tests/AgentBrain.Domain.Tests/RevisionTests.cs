using AgentBrain.Domain.Access;
using AgentBrain.Domain.Metadata;
using AgentBrain.Domain.Revisions;
using static AgentBrain.Domain.Tests.Node;

namespace AgentBrain.Domain.Tests;

public sealed class RevisionTests
{
    private static readonly Domain.Entries.EntryMetadata Metadata = EntryMetadataReader.Read(Entry(("tags", List("a", "b")))).Metadata!;

    [Fact]
    public void Revisions_have_the_documented_shape()
    {
        var revision = RevisionCalculator.Compute(Metadata, "Body\n");
        Assert.Matches("^r1-[a-z2-7]{26}$", revision.Value);
        Assert.True(Revision.TryParse(revision.Value, out _));
    }

    [Theory]
    [InlineData("Line one\r\nLine two\r\n")]
    [InlineData("Line one\nLine two")]
    [InlineData("﻿Line one\nLine two\n\n\n")]
    [InlineData("Line one\rLine two\r")]
    public void Line_endings_bom_and_trailing_newlines_do_not_change_the_revision(string body) =>
        Assert.Equal(RevisionCalculator.Compute(Metadata, "Line one\nLine two\n"), RevisionCalculator.Compute(Metadata, body));

    [Fact]
    public void Field_order_and_extension_fields_do_not_change_the_revision()
    {
        var reordered = EntryMetadataReader.Read(new MetadataMapping([.. Entry(("tags", List("a", "b")), ("x-local", "ignored")).Entries.Reverse()])).Metadata!;
        Assert.Equal(RevisionCalculator.Compute(Metadata, "x"), RevisionCalculator.Compute(reordered, "x"));
    }

    [Fact]
    public void Content_changes_change_the_revision()
    {
        var retitled = Metadata with { Title = "Save system v2" };
        Assert.NotEqual(RevisionCalculator.Compute(Metadata, "x"), RevisionCalculator.Compute(retitled, "x"));
        Assert.NotEqual(RevisionCalculator.Compute(Metadata, "x"), RevisionCalculator.Compute(Metadata, "y"));
        Assert.NotEqual(RevisionCalculator.Compute(Metadata, "a  b"), RevisionCalculator.Compute(Metadata, "a b"));
    }

    [Theory]
    [InlineData("r1-abc")]
    [InlineData("r2-aaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("r1-AAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void Malformed_revisions_are_rejected(string text) => Assert.False(Revision.TryParse(text, out _));

    [Theory]
    [InlineData(Capability.Read | Capability.Maintain | Capability.Publish, false, true, Capability.Read | Capability.Maintain | Capability.Publish)]
    [InlineData(Capability.Read | Capability.Maintain | Capability.Publish, true, true, Capability.Read)]
    [InlineData(Capability.Read | Capability.Propose, false, false, Capability.Read)]
    [InlineData(Capability.Propose, true, true, Capability.None)]
    public void Read_only_collections_cannot_be_elevated(Capability registered, bool readOnly, bool writable, Capability expected) =>
        Assert.Equal(expected, Capabilities.Effective(registered, readOnly, writable));
}
