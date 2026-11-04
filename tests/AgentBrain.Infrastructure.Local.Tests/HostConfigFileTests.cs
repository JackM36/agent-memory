using AgentBrain.Application.Common;
using AgentBrain.Application.Configuration;
using AgentBrain.Domain.Entries;
using AgentBrain.Domain.Identity;
using AgentBrain.Infrastructure.Local.Configuration;

namespace AgentBrain.Infrastructure.Local.Tests;

public sealed class HostConfigFileTests
{
    private static HostedCollection Hosted(string id, HostStorage storage = HostStorage.Sqlite, string? path = null) =>
        new(CollectionId.Parse(id), storage, path, "Unity library", new ApplicabilityOverrides(ApplicabilityScope.General, null, ["unity"]), ProvenanceSource.Curated, false, true);

    [Fact]
    public void Adding_a_collection_keeps_the_rest_and_stays_valid()
    {
        var initial = HostConfigFile.Initial("personal", "Personal knowledge");

        var added = HostConfigFile.AddCollection(initial, "host.json", Hosted("library.unity", HostStorage.Markdown, "collections/library.unity"));
        Assert.True(added.IsSuccess, added.Error?.Message);

        var parsed = HostConfigFile.Parse(added.Value, "host.json").Value;
        Assert.Equal(["personal", "library.unity"], parsed.Collections.Select(c => c.Id.Value));
        var unity = parsed.Collections[1];
        Assert.Equal(HostStorage.Markdown, unity.Storage);
        Assert.Equal(["unity"], unity.Defaults.Technologies);
        Assert.True(unity.ImmediatePublish);
        Assert.Equal(HostTransport.BehindProxy, parsed.Transport);
    }

    [Fact]
    public void An_existing_id_is_refused()
    {
        var initial = HostConfigFile.Initial("library", "Library");
        Assert.Equal(ErrorCode.ValidationFailed, HostConfigFile.AddCollection(initial, "host.json", Hosted("library")).Error?.Code);
    }

    [Fact]
    public void New_collection_descriptors_are_general_and_round_trip_their_id()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("brain-descriptor-").FullName, CollectionDescriptorFile.FileName);
        File.WriteAllText(path, CollectionDescriptorFile.Create("library.unity", "Unity library", ["unity"]));

        Assert.Equal("library.unity", CollectionDescriptorFile.ReadId(path));
        Assert.Contains("\"scope\": \"general\"", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Equal("Library Unity", CollectionDescriptorFile.TitleFrom("library.unity"));
    }
}
