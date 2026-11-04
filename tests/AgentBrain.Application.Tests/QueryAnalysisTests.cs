using AgentBrain.Application.Retrieval.QueryAnalysis;
using AgentBrain.Domain.Entries;

namespace AgentBrain.Application.Tests;

public sealed class QueryAnalysisTests
{
    private static readonly AliasGroup[] Aliases =
    [
        new(["ecs", "entity component system", "dots"]),
        new(["srp", "scriptable render pipeline"]),
    ];

    [Theory]
    [InlineData("SaveSystemV2", new[] { "save", "system", "v2" })]
    [InlineData("snake_case_name", new[] { "snake", "case", "name" })]
    [InlineData("SaveSystem.WriteSlot", new[] { "save", "system", "write", "slot" })]
    [InlineData("HTTPServer", new[] { "http", "server" })]
    [InlineData("useEffect", new[] { "use", "effect" })]
    [InlineData("plain", new[] { "plain" })]
    public void Identifiers_split_into_words(string identifier, string[] words) =>
        Assert.Equal(words, IdentifierSplitter.Split(identifier));

    [Fact]
    public void Stopwords_are_dropped_and_each_word_is_one_concept()
    {
        Assert.True(QueryAnalyzer.TryAnalyze("How do I change the save slots after a crash?", [], out var terms, out _));
        Assert.Equal(["change", "save", "slots", "crash"], terms.Concepts.Select(c => c.Original));
    }

    [Fact]
    public void Identifiers_match_their_split_and_joined_forms()
    {
        Assert.True(QueryAnalyzer.TryAnalyze("SaveSystem", [], out var terms, out _));
        var concept = Assert.Single(terms.Concepts);
        Assert.Equal(["savesystem", "save system"], concept.Terms);
    }

    [Fact]
    public void Aliases_add_equivalent_terms_not_new_concepts()
    {
        Assert.True(QueryAnalyzer.TryAnalyze("ECS baking", Aliases, out var terms, out _));
        Assert.Equal(2, terms.Concepts.Count);
        Assert.Equal(["ecs", "entity component system", "dots"], terms.Concepts[0].Terms);
    }

    [Fact]
    public void Multi_word_alias_terms_are_matched_as_one_concept()
    {
        Assert.True(QueryAnalyzer.TryAnalyze("custom scriptable render pipeline pass", Aliases, out var terms, out _));
        Assert.Equal(["custom", "scriptable render pipeline", "pass"], terms.Concepts.Select(c => c.Original));
        Assert.Contains("srp", terms.Concepts[1].Terms);
    }

    [Fact]
    public void Repeated_words_are_one_concept_and_punctuation_is_ignored()
    {
        Assert.True(QueryAnalyzer.TryAnalyze("\"cache\" cache; (cache) c# node.js -- *", [], out var terms, out _));
        Assert.Equal(["cache", "c#", "node.js"], terms.Concepts.Select(c => c.Original));
    }

    [Fact]
    public void Oversized_queries_are_rejected_not_truncated()
    {
        Assert.False(QueryAnalyzer.TryAnalyze(new string('a', QueryAnalyzer.MaxQueryLength + 1), [], out _, out var tooLong));
        Assert.Contains("limit", tooLong, StringComparison.Ordinal);

        var manyWords = string.Join(' ', Enumerable.Range(0, 40).Select(i => "word" + i));
        Assert.False(QueryAnalyzer.TryAnalyze(manyWords, [], out _, out var tooMany));
        Assert.Contains("32", tooMany, StringComparison.Ordinal);
    }

    [Fact]
    public void Mentions_detects_technology_names_in_any_term()
    {
        Assert.True(QueryAnalyzer.TryAnalyze("react hooks", [], out var terms, out _));
        Assert.True(QueryAnalyzer.Mentions(terms, ["web", "react"]));
        Assert.False(QueryAnalyzer.Mentions(terms, ["unity"]));
    }
}
