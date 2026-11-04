using AgentBrain.Domain.Entries;
using AgentBrain.Domain.Metadata;
using AgentBrain.Domain.Validation;
using static AgentBrain.Domain.Tests.Node;

namespace AgentBrain.Domain.Tests;

public sealed class MetadataTests
{
    [Fact]
    public void Minimal_entry_reads_and_validates_with_collection_defaults()
    {
        var result = EntryMetadataReader.Read(Entry());

        Assert.Empty(result.Issues);
        var validation = EntryValidator.Validate(result.Metadata!, Collection());
        Assert.True(validation.IsValid);
        Assert.Equal(ApplicabilityScope.Project, validation.Applicability!.Scope);
        Assert.Equal(["space-game"], validation.Applicability.Projects);
    }

    [Fact]
    public void Full_entry_reads_every_field()
    {
        var result = EntryMetadataReader.Read(Entry(
            ("kind", "uncertainty"),
            ("tags", List("Save System", "serialization", "save_system")),
            ("applies", Map(
                ("scope", "general"),
                ("technologies", List("Unity", "C#")),
                ("packages", List(Map(("name", "unity"), ("versions", ">=2021.3")))),
                ("platforms", List("iOS")))),
            ("files", List("Assets/Scripts/Save/**")),
            ("symbols", List("SaveSystem")),
            ("source", "observed"),
            ("evidence", List("user confirmed 2026-11-02")),
            ("verified", Map(("on", "2026-11-02"), ("by", "repeated-use"))),
            ("publication", "published"),
            ("resolution", "unresolved"),
            ("blocking", true),
            ("created", "2026-11-01"),
            ("updated", "2026-11-02"),
            ("x-owner", "jack")));

        Assert.Empty(result.Issues);
        var m = result.Metadata!;
        Assert.Equal(["save-system", "serialization"], m.Tags);
        Assert.Equal(["unity", "c#"], m.Applies!.Technologies);
        Assert.Equal(["ios"], m.Applies.Platforms);
        Assert.Equal(new PackageConstraint("unity", ">=2021.3"), Assert.Single(m.Applies.Packages!));
        Assert.Equal(new Verification(new DateOnly(2026, 11, 2), VerificationMethod.RepeatedUse), m.Verified);
        Assert.True(m.IsBlockingUncertainty);
        Assert.Equal("x-owner", Assert.Single(m.Extensions).Key);
    }

    [Fact]
    public void Unknown_fields_are_errors_but_extension_fields_are_kept()
    {
        var result = EntryMetadataReader.Read(Entry(("confidence", "high"), ("x-confidence", "high")));

        Assert.Null(result.Metadata);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(IssueCodes.UnknownField, issue.Code);
        Assert.Equal("confidence", issue.Field);
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("id")]
    [InlineData("kind")]
    [InlineData("title")]
    [InlineData("summary")]
    public void Required_fields_are_reported(string field)
    {
        var mapping = new MetadataMapping([.. Entry().Entries.Where(e => e.Key != field)]);
        var result = EntryMetadataReader.Read(mapping);

        Assert.Null(result.Metadata);
        Assert.Contains(result.Issues, i => i.Code == IssueCodes.Required && i.Field == field);
    }

    [Fact]
    public void A_newer_schema_is_reported_without_further_errors()
    {
        var result = EntryMetadataReader.Read(Entry(("schema", 2), ("mystery", "x")));

        var issue = Assert.Single(result.Issues);
        Assert.Equal(IssueCodes.UnsupportedSchema, issue.Code);
    }

    [Theory]
    [InlineData("kind", "rule")]
    [InlineData("id", "Save System")]
    [InlineData("created", "02/11/2026")]
    [InlineData("blocking", "yes")]
    [InlineData("superseded-by", "https://example.com/x")]
    [InlineData("source", "guessed")]
    public void Invalid_values_are_reported_with_their_field(string field, string value)
    {
        var result = EntryMetadataReader.Read(Entry((field, value)));

        Assert.Null(result.Metadata);
        Assert.Contains(result.Issues, i => i.Field == field);
    }

    [Fact]
    public void Duplicate_fields_are_reported()
    {
        var mapping = new MetadataMapping([.. Entry().Entries, new("title", new MetadataScalar("Again"))]);
        Assert.Contains(EntryMetadataReader.Read(mapping).Issues, i => i.Code == IssueCodes.DuplicateField);
    }

    [Fact]
    public void Missing_scope_without_a_collection_default_is_invalid()
    {
        var metadata = EntryMetadataReader.Read(Entry()).Metadata!;
        var collection = Collection() with { Defaults = new CollectionDefaults() };

        var validation = EntryValidator.Validate(metadata, collection);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Issues, i => i.Field == "applies.scope");
    }

    [Fact]
    public void Entry_values_override_collection_defaults_field_by_field()
    {
        var metadata = EntryMetadataReader.Read(Entry(("applies", Map(("technologies", List("unity")))))).Metadata!;
        var validation = EntryValidator.Validate(metadata, Collection());

        Assert.Equal(ApplicabilityScope.Project, validation.Applicability!.Scope);
        Assert.Equal(["space-game"], validation.Applicability.Projects);
        Assert.Equal(["unity"], validation.Applicability.Technologies);
    }

    public static TheoryData<string, (string, object?)[]> InvalidEntries => new()
    {
        { "summary", [("summary", new string('x', 241))] },
        { "title", [("title", "Two\nlines")] },
        { "resolution", [("resolution", "unresolved")] },
        { "resolution", [("kind", "uncertainty")] },
        { "blocking", [("kind", "uncertainty"), ("resolution", "resolved"), ("blocking", true)] },
        { "resolved-by", [("kind", "uncertainty"), ("resolution", "unresolved"), ("resolved-by", "entry://a/b")] },
        { "applies.projects", [("applies", Map(("scope", "general"), ("projects", List("space-game"))))] },
        { "applies.projects", [("applies", Map(("scope", "project"), ("projects", List())))] },
        { "applies.packages[0].versions", [("applies", Map(("packages", List(Map(("name", "unity"), ("versions", "^2021"))))))] },
        { "files[0]", [("files", List("../secrets/**"))] },
        { "superseded-by", [("superseded-by", "entry://space-game.memory/save-system")] },
        { "origin", [("origin", "entry://a/b#section")] },
    };

    [Theory]
    [MemberData(nameof(InvalidEntries))]
    public void Cross_field_rules_are_enforced(string field, (string, object?)[] overrides)
    {
        var read = EntryMetadataReader.Read(Entry(overrides));
        Assert.NotNull(read.Metadata);

        var validation = EntryValidator.Validate(read.Metadata, Collection());

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Issues, i => i.Field == field);
    }

    [Fact]
    public void Collections_may_lower_the_summary_limit()
    {
        var metadata = EntryMetadataReader.Read(Entry(("summary", new string('x', 150)))).Metadata!;
        Assert.True(EntryValidator.Validate(metadata, Collection()).IsValid);
        Assert.False(EntryValidator.Validate(metadata, Collection(summaryMax: 100)).IsValid);
    }

    [Fact]
    public void Collection_descriptors_require_a_default_scope_and_sane_limits()
    {
        Assert.Empty(Collection().Validate());
        Assert.Contains(Collection(defaults: ApplicabilityOverrides.None).Validate(), i => i.Field == "defaults.applies.scope");
        Assert.Contains(Collection(new ApplicabilityOverrides(ApplicabilityScope.Project)).Validate(), i => i.Field == "defaults.applies.projects");
        Assert.Contains(Collection(summaryMax: 500).Validate(), i => i.Field == "limits.summaryMax");
    }

    [Fact]
    public void Writer_round_trips_through_the_reader()
    {
        var original = EntryMetadataReader.Read(Entry(
            ("tags", List("a", "b")),
            ("applies", Map(("scope", "general"), ("packages", List(Map(("name", "unity"), ("versions", ">=2021.3")))))),
            ("verified", Map(("on", "2026-11-02"), ("by", "code"))),
            ("superseded-by", "entry://a/b"),
            ("x-note", "kept"))).Metadata!;

        var reread = EntryMetadataReader.Read(EntryMetadataWriter.ToNode(original)).Metadata!;

        Assert.Equal(EntryMetadataWriter.ToNode(original).Entries.Select(e => e.Key), EntryMetadataWriter.ToNode(reread).Entries.Select(e => e.Key));
        Assert.Equal(Revisions.RevisionCalculator.Compute(original, "body"), Revisions.RevisionCalculator.Compute(reread, "body"));
    }

    [Fact]
    public void Writer_emits_fields_in_canonical_order()
    {
        var metadata = EntryMetadataReader.Read(Entry(("updated", "2026-11-02"), ("tags", List("a")))).Metadata!;
        var keys = EntryMetadataWriter.ToNode(metadata).Entries.Select(e => e.Key).ToList();
        Assert.Equal(["schema", "id", "kind", "title", "summary", "tags", "updated"], keys);
    }
}
