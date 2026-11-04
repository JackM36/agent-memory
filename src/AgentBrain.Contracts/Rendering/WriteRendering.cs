using System.Globalization;
using System.Text;
using System.Text.Json;
using AgentBrain.Application.Abstractions.Proposals;
using AgentBrain.Application.Capture;
using AgentBrain.Application.Operations.Maintenance;
using AgentBrain.Application.Operations.Publishing;
using AgentBrain.Application.Writing;
using AgentBrain.Contracts.V1;

namespace AgentBrain.Contracts.Rendering;

/// <summary>
/// Output for write operations (maintenance, proposals, publishing, batches) in the text and JSON formats.
/// Write results are small and unbudgeted, so they live outside <see cref="Application.Abstractions.Support.IResponseRenderer"/>.
/// </summary>
public static class WriteRendering
{
    public static string Written(WriteResult result, string action, bool json)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (json)
        {
            return JsonSerializer.Serialize(
                new WriteResponseDto(ProtocolVersions.Protocol, result.Ref.ToString(), result.Revision?.Value, [.. result.Similar.Select(Similar)]),
                ContractsJsonContext.Default.WriteResponseDto);
        }

        var text = new StringBuilder().Append(CultureInfo.InvariantCulture, $"{action} {result.Ref}");
        if (result.Revision is { } revision)
        {
            text.Append(CultureInfo.InvariantCulture, $" · rev {revision}");
        }

        text.Append('\n');
        if (result.Similar.Count > 0)
        {
            text.Append("similar existing entries — update one of these instead of keeping a duplicate:\n");
            foreach (var s in result.Similar)
            {
                text.Append(CultureInfo.InvariantCulture, $"  {s.Ref} {s.Summary}\n");
            }
        }

        return text.ToString();
    }

    public static string SimilarEntries(IReadOnlyList<SimilarEntry> similar, bool json)
    {
        ArgumentNullException.ThrowIfNull(similar);
        if (json)
        {
            return JsonSerializer.Serialize(new SimilarResponseDto(ProtocolVersions.Protocol, [.. similar.Select(Similar)]), ContractsJsonContext.Default.SimilarResponseDto);
        }

        if (similar.Count == 0)
        {
            return "no similar entries\n";
        }

        var text = new StringBuilder();
        foreach (var s in similar)
        {
            text.Append(CultureInfo.InvariantCulture, $"{s.Ref} {s.Summary}\n");
        }

        return text.ToString();
    }

    public static string Proposals(IReadOnlyList<Proposal> proposals, bool json, bool withBody = false)
    {
        ArgumentNullException.ThrowIfNull(proposals);
        if (json)
        {
            return JsonSerializer.Serialize(new ProposalsResponseDto(ProtocolVersions.Protocol, [.. proposals.Select(p => ToDto(p, withBody))]), ContractsJsonContext.Default.ProposalsResponseDto);
        }

        if (proposals.Count == 0)
        {
            return "no proposals\n";
        }

        var text = new StringBuilder();
        foreach (var p in proposals)
        {
            var c = p.Change;
            text.Append(CultureInfo.InvariantCulture,
                $"{p.Id} [{p.Status.ToString().ToLowerInvariant()}] {c.Kind.ToString().ToLowerInvariant()} {c.Target}{(c.Metadata is null ? "" : " — " + c.Metadata.Title)} · by {p.ProposedBy} · {p.CreatedAt:yyyy-MM-dd HH:mm}Z\n");
            if (p.Note is not null)
            {
                text.Append("  note: ").Append(p.Note).Append('\n');
            }

            if (p.ReviewedBy is not null)
            {
                text.Append(CultureInfo.InvariantCulture, $"  reviewed by {p.ReviewedBy}{(p.ReviewNote is null ? "" : ": " + p.ReviewNote)}\n");
            }

            if (withBody && c.Metadata is not null)
            {
                text.Append(CultureInfo.InvariantCulture, $"  summary: {c.Metadata.Summary}\n  base revision: {c.BaseRevision?.Value ?? "(new entry)"}\n");
                text.Append("── proposed body\n").Append(c.Body).Append("── end proposed body\n");
            }
        }

        return text.ToString();
    }

    public static string Published(IReadOnlyList<PublishedChange> published, bool json)
    {
        ArgumentNullException.ThrowIfNull(published);
        if (json)
        {
            return JsonSerializer.Serialize(
                new PublishResponseDto(ProtocolVersions.Protocol, [.. published.Select(p => new PublishedDto(p.Target.ToString(), Kind(p.Kind), p.Revision?.Value))]),
                ContractsJsonContext.Default.PublishResponseDto);
        }

        var text = new StringBuilder();
        foreach (var p in published)
        {
            text.Append(CultureInfo.InvariantCulture, $"published {Kind(p.Kind)} {p.Target}{(p.Revision is null ? "" : " · rev " + p.Revision)}\n");
        }

        return text.ToString();
    }

    public static string Batches(IReadOnlyList<BatchStatus> batches, bool json)
    {
        ArgumentNullException.ThrowIfNull(batches);
        if (json)
        {
            return JsonSerializer.Serialize(
                new BatchesResponseDto(ProtocolVersions.Protocol, [.. batches.Select(b => new BatchDto(
                    b.Batch.Id, b.Batch.Collection.Value, b.Batch.StartedAt.ToString("O", CultureInfo.InvariantCulture), b.Batch.By.ToString(),
                    [.. b.Items.Select(i => new BatchItemDto(i.Change.Target.ToString(), Kind(i.Change.Kind), i.State.ToString().ToLowerInvariant()))]))]),
                ContractsJsonContext.Default.BatchesResponseDto);
        }

        if (batches.Count == 0)
        {
            return "no interrupted batches\n";
        }

        var text = new StringBuilder();
        foreach (var b in batches)
        {
            text.Append(CultureInfo.InvariantCulture, $"batch {b.Batch.Id} · {b.Batch.Collection} · started {b.Batch.StartedAt:yyyy-MM-dd HH:mm}Z by {b.Batch.By}\n");
            foreach (var i in b.Items)
            {
                text.Append(CultureInfo.InvariantCulture, $"  {i.State.ToString().ToLowerInvariant(),-8} {Kind(i.Change.Kind)} {i.Change.Target}\n");
            }
        }

        return text.ToString();
    }

    private static SimilarDto Similar(SimilarEntry s) => new(s.Ref.ToString(), s.Title, s.Summary);

    private static string Kind(ChangeKind kind) => kind.ToString().ToLowerInvariant();

    private static ProposalDto ToDto(Proposal p, bool withBody) => new(
        p.Id,
        p.Status.ToString().ToLowerInvariant(),
        Kind(p.Change.Kind),
        p.Change.Target.ToString(),
        p.Change.BaseRevision?.Value,
        p.Change.Metadata?.Title,
        p.Change.Metadata?.Summary,
        p.ProposedBy.ToString(),
        p.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
        p.Note,
        p.ReviewedBy?.ToString(),
        p.ReviewNote,
        withBody ? p.Change.Body : null);
}
