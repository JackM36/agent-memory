using System.Text.Json;
using AgentBrain.Application.Abstractions.Proposals;
using AgentBrain.Application.Common;

namespace AgentBrain.Infrastructure.Local.Records;

/// <summary>
/// Proposals as one JSON file each in <c>.agent-brain/proposals/</c> (committed by default, so they can be
/// reviewed in Git). Files are written atomically; nothing here is treated as cache.
/// </summary>
public sealed class FileProposalStore(string directory) : IProposalStore
{
    public async ValueTask<Result> SaveAsync(Proposal proposal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        Directory.CreateDirectory(directory);
        var json = RecordJson.Write(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("schema", 1);
            w.WriteString("id", proposal.Id);
            w.WriteString("status", proposal.Status.ToString().ToLowerInvariant());
            w.WriteString("createdAt", RecordJson.Time(proposal.CreatedAt));
            RecordJson.WriteAttribution(w, "proposedBy", proposal.ProposedBy);
            w.WriteString("note", proposal.Note);
            RecordJson.WriteChange(w, "change", proposal.Change);
            RecordJson.WriteAttribution(w, "reviewedBy", proposal.ReviewedBy);
            w.WriteString("reviewedAt", proposal.ReviewedAt is { } at ? RecordJson.Time(at) : null);
            w.WriteString("reviewNote", proposal.ReviewNote);
            w.WriteEndObject();
        });

        var path = Path.Combine(directory, proposal.Id + ".json");
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, json + "\n", cancellationToken);
        File.Move(temp, path, overwrite: true);
        return Result.Success;
    }

    public async ValueTask<Proposal?> FindAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (id.IndexOfAny(['/', '\\', '.']) >= 0)
        {
            return null;
        }

        var path = Path.Combine(directory, id + ".json");
        return File.Exists(path) ? Read(await File.ReadAllTextAsync(path, cancellationToken)) : null;
    }

    public async ValueTask<IReadOnlyList<Proposal>> ListAsync(ProposalStatus? status, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var proposals = new List<Proposal>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            var proposal = Read(await File.ReadAllTextAsync(file, cancellationToken));
            if (status is null || proposal.Status == status)
            {
                proposals.Add(proposal);
            }
        }

        return proposals;
    }

    private static Proposal Read(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return new Proposal(
            root.GetProperty("id").GetString()!,
            RecordJson.ReadChange(root.GetProperty("change")),
            RecordJson.ReadAttribution(root, "proposedBy")!,
            RecordJson.ReadTime(root, "createdAt")!.Value,
            root.TryGetProperty("note", out var note) ? note.GetString() : null,
            Enum.Parse<ProposalStatus>(root.GetProperty("status").GetString()!, ignoreCase: true),
            RecordJson.ReadAttribution(root, "reviewedBy"),
            RecordJson.ReadTime(root, "reviewedAt"),
            root.TryGetProperty("reviewNote", out var review) ? review.GetString() : null);
    }
}
