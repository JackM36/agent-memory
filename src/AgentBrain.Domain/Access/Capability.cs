using AgentBrain.Domain.Entries;

namespace AgentBrain.Domain.Access;

/// <summary>What a caller may do with a collection (spec §4.4). Capabilities belong to collections and callers, not entry kinds.</summary>
[Flags]
public enum Capability
{
    None = 0,
    Read = 1,
    Maintain = 2,
    Propose = 4,
    Publish = 8,
}

public static class Capabilities
{
    public static readonly WireTable<Capability> Names = new(
        (Capability.Read, "read"), (Capability.Maintain, "maintain"), (Capability.Propose, "propose"), (Capability.Publish, "publish"));

    /// <summary>
    /// Effective capabilities (plan §7.4): the registration's capabilities, capped to read-only when the
    /// collection declares itself read-only or its storage is not writable. Nothing can elevate them.
    /// </summary>
    public static Capability Effective(Capability registered, bool collectionReadOnly, bool storageWritable) =>
        collectionReadOnly || !storageWritable ? registered & Capability.Read : registered;

    public static IEnumerable<string> ToNames(Capability capabilities) =>
        Names.Names.Where(name => Names.TryParse(name, out var c) && capabilities.HasFlag(c));
}
