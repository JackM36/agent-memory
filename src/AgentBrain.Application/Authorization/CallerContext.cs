using AgentBrain.Application.Common;
using AgentBrain.Application.Retrieval;
using AgentBrain.Domain.Access;
using AgentBrain.Domain.Identity;

namespace AgentBrain.Application.Authorization;

/// <summary>
/// Who is calling and what they were granted. Local CLI and MCP callers act with the collection's own
/// capabilities (filesystem access); remote callers are limited further by their API key's scopes.
/// </summary>
public sealed record CallerContext(string Actor, string Via, Func<CollectionId, Capability>? Grants = null)
{
    public static CallerContext Local(string via) => new(Environment.UserName, via);

    /// <summary>Capabilities this caller has on a collection: the collection's capabilities, capped by grants.</summary>
    public Capability Effective(CollectionInfo collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        return Grants is null ? collection.Capabilities : collection.Capabilities & Grants(collection.Id);
    }
}

/// <summary>The single capability check every operation uses, so no endpoint can bypass it (spec §10.1, §15.3).</summary>
public static class CapabilityResolver
{
    public static Result Require(CallerContext caller, CollectionInfo collection, Capability required)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(collection);
        if ((caller.Effective(collection) & required) == required)
        {
            return Result.Success;
        }

        var name = string.Join(", ", Capabilities.ToNames(required));
        if (collection.ReadOnly)
        {
            return new Error(ErrorCode.ReadOnly, $"Collection '{collection.Id}' is read-only. Submit a local proposal instead.");
        }

        return new Error(ErrorCode.Forbidden, required == Capability.Maintain
            ? $"Collection '{collection.Id}' does not allow direct changes ('{name}'). Use brain propose."
            : $"This caller lacks '{name}' on collection '{collection.Id}'.");
    }
}
