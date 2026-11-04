using AgentBrain.Application.Abstractions.Storage;
using AgentBrain.Application.Retrieval;
using AgentBrain.Domain.Identity;

namespace AgentBrain.Application.Writing;

/// <summary>A collection this process can write to (or propose against), with its storage.</summary>
public sealed record WritableCollection(CollectionInfo Info, IEntryReader Reader, IEntryWriter? Writer, bool ImmediatePublish);

/// <summary>The collections hosted by this process, by ID. Remote collections are not here: they are written through their host.</summary>
public sealed class CollectionCatalog(IReadOnlyList<WritableCollection> collections)
{
    private readonly Dictionary<CollectionId, WritableCollection> _byId = collections.ToDictionary(c => c.Info.Id);

    public IReadOnlyList<WritableCollection> All { get; } = collections;

    public WritableCollection? Find(CollectionId id) => _byId.GetValueOrDefault(id);
}
