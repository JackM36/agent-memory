using System.Globalization;
using AgentBrain.Application.Abstractions.Support;

namespace AgentBrain.Infrastructure.Local.Records;

/// <summary>Audit records appended to <c>local/state/audit/YYYY-MM.jsonl</c> (plan §5.12). Kept indefinitely; not a content snapshot.</summary>
public sealed class JsonlAuditLog(string directory) : IAuditLog
{
    public async ValueTask AppendAsync(AuditRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        Directory.CreateDirectory(directory);
        var json = RecordJson.Write(w =>
        {
            w.WriteStartObject();
            w.WriteString("at", RecordJson.Time(record.At));
            RecordJson.WriteAttribution(w, "actor", record.Actor);
            w.WriteString("action", record.Action);
            w.WriteString("target", record.Target?.ToString());
            w.WriteString("outcome", record.Outcome);
            w.WriteString("detail", record.Detail);
            w.WriteEndObject();
        }, indented: false);

        var path = Path.Combine(directory, record.At.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture) + ".jsonl");
        await File.AppendAllTextAsync(path, json + "\n", cancellationToken);
    }
}
