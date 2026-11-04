using AgentBrain.Application.Common;
using AgentBrain.Application.Retrieval;
using AgentBrain.Domain.Identity;
using AgentBrain.Domain.References;

namespace AgentBrain.Application.Operations.Relationships;

/// <summary>Outgoing references, paginated backlinks and canonical links (spec §6.2). Nothing is fetched recursively.</summary>
public sealed class RelationsOperation(RetrievalCoordinator coordinator, IndexFreshness freshness)
{
    public async ValueTask<Result<IReadOnlyList<OutgoingReference>>> ReferencesAsync(EntryRef entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await freshness.EnsureFreshAsync(cancellationToken);
        return coordinator.ProviderFor(entry.Collection) is { } provider
            ? await provider.ReferencesAsync(entry, cancellationToken)
            : Error.NotFound($"Collection '{entry.Collection}' is not configured.");
    }

    public async ValueTask<Result<Page<Backlink>>> BacklinksAsync(EntryRef entry, string? cursor, int pageSize, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await freshness.EnsureFreshAsync(cancellationToken);

        // Phase 2: backlinks come from the local index, which covers every local collection.
        var provider = coordinator.Providers.Count > 0 ? coordinator.Providers[0] : null;
        return provider is null
            ? new Page<Backlink>([], null)
            : await provider.BacklinksAsync(entry, cursor, pageSize, cancellationToken);
    }

    /// <summary>The canonical Markdown link for an entry or section. The anchor may be given as heading text.</summary>
    public static Result<string> CanonicalLink(EntryRef entry, string? section, string? label)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var anchor = section is null ? entry.Anchor : Domain.Sections.HeadingAnchor.IsValid(section) ? section : Domain.Sections.HeadingAnchor.FromHeadingText(section);
        var target = entry with { Anchor = anchor };
        return $"[{label ?? target.ToString()}]({EntryLink.Format(target)})";
    }
}
