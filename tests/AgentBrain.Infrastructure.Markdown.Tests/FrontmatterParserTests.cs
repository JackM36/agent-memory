using AgentBrain.Domain.Metadata;
using AgentBrain.Infrastructure.Markdown.Collections;
using AgentBrain.Infrastructure.Markdown.Frontmatter;

namespace AgentBrain.Infrastructure.Markdown.Tests;

public sealed class FrontmatterParserTests
{
    [Fact]
    public void Splits_frontmatter_from_body_with_line_numbers()
    {
        const string text = "---\nschema: 1\nid: a\ntags: [x, y]\nverified: { on: 2026-11-02, by: code }\n---\n# Heading\nBody\n";

        var (document, error) = FrontmatterParser.Parse(text);

        Assert.Null(error);
        Assert.Equal("# Heading\nBody\n", document!.Body);
        Assert.Equal(7, document.BodyLine);
        var tags = Assert.IsType<MetadataSequence>(document.Metadata.Entries.Single(e => e.Key == "tags").Value);
        Assert.Equal(["x", "y"], tags.Items.Cast<MetadataScalar>().Select(s => s.Value));
        Assert.Equal(4, document.Metadata.Entries.Single(e => e.Key == "tags").Value.Line);
    }

    [Fact]
    public void Crlf_and_bom_are_normalised()
    {
        var (document, error) = FrontmatterParser.Parse("﻿---\r\nschema: 1\r\n---\r\nBody\r\n");
        Assert.Null(error);
        Assert.Equal("Body\n", document!.Body);
    }

    [Fact]
    public void Empty_values_become_null_and_quoted_values_stay_text()
    {
        var (document, _) = FrontmatterParser.Parse("---\nsummary:\nblocking: \"true\"\n---\n");
        var entries = document!.Metadata.Entries.ToDictionary(e => e.Key, e => e.Value);

        Assert.IsType<MetadataNull>(entries["summary"]);
        Assert.Equal(new MetadataScalar("true", true, 3), entries["blocking"]);
    }

    [Theory]
    [InlineData("no frontmatter", "does not start")]
    [InlineData("---\nschema: 1\n", "not closed")]
    [InlineData("---\nschema: [1\n---\n", "Invalid YAML")]
    [InlineData("---\n- a\n- b\n---\n", "single set of fields")]
    public void Malformed_files_are_reported(string text, string message)
    {
        var (document, error) = FrontmatterParser.Parse(text);
        Assert.Null(document);
        Assert.Contains(message, error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Collection_files_need_a_default_scope()
    {
        const string json = """{ "schema": 1, "id": "kb", "title": "KB", "defaults": { "applies": { "scope": "project" } } }""";
        var result = CollectionFileReader.Parse(json, "collection.json");
        Assert.False(result.IsSuccess);
        Assert.Contains("defaults.applies.projects", result.Error.Message, StringComparison.Ordinal);
    }
}
