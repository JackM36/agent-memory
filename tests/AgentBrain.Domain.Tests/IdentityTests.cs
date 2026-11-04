using AgentBrain.Domain.Identity;
using AgentBrain.Domain.References;

namespace AgentBrain.Domain.Tests;

public sealed class IdentityTests
{
    [Theory]
    [InlineData("space-game.memory", true)]
    [InlineData("unity.serialization", true)]
    [InlineData("a", true)]
    [InlineData("Space-Game", false)]
    [InlineData("space--game", false)]
    [InlineData("-space", false)]
    [InlineData("space.", false)]
    [InlineData("space game", false)]
    [InlineData("", false)]
    public void Collection_id_grammar(string value, bool valid) => Assert.Equal(valid, CollectionId.IsValid(value));

    [Fact]
    public void Collection_id_length_limit()
    {
        Assert.True(CollectionId.IsValid(new string('a', 64)));
        Assert.False(CollectionId.IsValid(new string('a', 65)));
    }

    [Theory]
    [InlineData("save-system", true)]
    [InlineData("2026-11-02-save-corruption", true)]
    [InlineData("save.system", false)]
    [InlineData("save_system", false)]
    [InlineData("Save", false)]
    public void Entry_id_grammar(string value, bool valid) => Assert.Equal(valid, EntryId.IsValid(value));

    [Theory]
    [InlineData("Save System: Slot Layout & Gotchas", "save-system-slot-layout-gotchas")]
    [InlineData("  Ünïcödé  Größe ", "unicode-grosse")]
    [InlineData("Ærøskøbing Œuvre", "aeroskobing-oeuvre")]
    [InlineData("日本語 notes", "notes")]
    [InlineData("[SerializeReference] pitfalls", "serializereference-pitfalls")]
    [InlineData("!!!", "entry")]
    public void Ids_are_generated_from_titles(string title, string expected) => Assert.Equal(expected, EntryId.FromTitle(title).Value);

    [Fact]
    public void Generated_ids_respect_the_length_limit_and_never_end_with_a_dash()
    {
        var id = EntryId.FromTitle(string.Join(' ', Enumerable.Repeat("word", 40)));
        Assert.True(id.Value.Length <= EntryId.MaxLength);
        Assert.False(id.Value.EndsWith('-'));
    }

    [Fact]
    public void Collision_suffix_keeps_ids_within_the_limit()
    {
        Assert.Equal("save-system-2", EntryId.Parse("save-system").WithCollisionSuffix(2).Value);
        var longId = EntryId.Parse(new string('a', 80));
        Assert.Equal(EntryId.MaxLength, longId.WithCollisionSuffix(12).Value.Length);
        Assert.EndsWith("-12", longId.WithCollisionSuffix(12).Value, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("space-game.memory/save-system", "space-game.memory", "save-system", null)]
    [InlineData("unity.serialization/serialize-reference#pitfalls", "unity.serialization", "serialize-reference", "pitfalls")]
    public void Entry_refs_parse(string text, string collection, string entry, string? anchor)
    {
        var reference = EntryRef.Parse(text);
        Assert.Equal(collection, reference.Collection.Value);
        Assert.Equal(entry, reference.Entry.Value);
        Assert.Equal(anchor, reference.Anchor);
        Assert.Equal(text, reference.ToString());
    }

    [Theory]
    [InlineData("save-system")]
    [InlineData("a/b/c")]
    [InlineData("/b")]
    [InlineData("a/B")]
    [InlineData("a/b#Bad Anchor")]
    public void Malformed_entry_refs_are_rejected(string text) => Assert.False(EntryRef.TryParse(text, out _));

    [Fact]
    public void Entry_refs_have_value_equality() =>
        Assert.Equal(EntryRef.Parse("a/b#c"), EntryRef.Parse("a/b#c"));

    [Theory]
    [InlineData("entry://unity.serialization/serialize-reference", "unity.serialization/serialize-reference")]
    [InlineData("entry://c/e#managed-references", "c/e#managed-references")]
    [InlineData("entry://c/e#gr%C3%B6%C3%9Fe", "c/e#größe")]
    public void Entry_links_parse(string link, string expected)
    {
        Assert.True(EntryLink.TryParse(link, out var reference));
        Assert.Equal(expected, reference.ToString());
        Assert.Equal(link, EntryLink.Format(reference));
    }

    [Theory]
    [InlineData("https://c/e")]
    [InlineData("entry://c")]
    [InlineData("entry://c/e?x=1")]
    [InlineData("entry://host.example.com/c/e")]
    [InlineData("ENTRY://c/e")]
    [InlineData("entry://c/e#")]
    public void Malformed_entry_links_are_rejected(string link) => Assert.False(EntryLink.TryParse(link, out _));
}
