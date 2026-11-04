using System.Text.Json;
using AgentBrain.Application.Abstractions.Proposals;
using AgentBrain.Domain.Identity;

namespace AgentBrain.Infrastructure.Local.Records;

/// <summary>Batch markers in <c>local/state/batches/</c> (plan §5.12, A6). A file that remains means the batch was interrupted.</summary>
public sealed class FileBatchJournal(string directory) : IBatchJournal
{
    public async ValueTask BeginAsync(BatchRecord batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        Directory.CreateDirectory(directory);
        var json = RecordJson.Write(w =>
        {
            w.WriteStartObject();
            w.WriteString("id", batch.Id);
            w.WriteString("collection", batch.Collection.Value);
            w.WriteString("startedAt", RecordJson.Time(batch.StartedAt));
            RecordJson.WriteAttribution(w, "by", batch.By);
            w.WriteStartArray("changes");
            foreach (var change in batch.Changes)
            {
                w.WriteStartObject();
                RecordJson.WriteChange(w, "change", change);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        });

        var path = Path.Combine(directory, batch.Id + ".json");
        await File.WriteAllTextAsync(path + ".tmp", json, cancellationToken);
        File.Move(path + ".tmp", path, overwrite: true);
    }

    public ValueTask CompleteAsync(string id, CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, id + ".json");
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask<IReadOnlyList<BatchRecord>> PendingAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var batches = new List<BatchRecord>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(file, cancellationToken));
            var root = document.RootElement;
            batches.Add(new BatchRecord(
                root.GetProperty("id").GetString()!,
                CollectionId.Parse(root.GetProperty("collection").GetString()!),
                RecordJson.ReadTime(root, "startedAt")!.Value,
                RecordJson.ReadAttribution(root, "by")!,
                [.. root.GetProperty("changes").EnumerateArray().Select(c => RecordJson.ReadChange(c.GetProperty("change")))]));
        }

        return batches;
    }
}
