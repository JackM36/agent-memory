using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AgentBrain.Application.Abstractions.Support;

namespace AgentBrain.Infrastructure.Local.Logging;

/// <summary>Appends searches to <c>local/state/query-log.jsonl</c>, one JSON object per line.</summary>
public sealed class JsonlQueryLog(string path) : IQueryLog
{
    public async ValueTask AppendAsync(QueryLogRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("at", record.At.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteString("query", record.Query);
            writer.WriteString("project", record.Context.ProjectId);
            WriteList(writer, "files", record.Context.Files);
            WriteList(writer, "symbols", record.Context.Symbols);
            WriteList(writer, "results", record.Results);
            WriteList(writer, "blocking", record.Blocking);
            writer.WriteEndObject();
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.AppendAllTextAsync(path, Encoding.UTF8.GetString(buffer.WrittenSpan) + "\n", cancellationToken);
        }
        catch (IOException)
        {
            // Logging is best effort: a locked or full disk must not fail the search.
        }
        catch (UnauthorizedAccessException)
        {
            // Same: never fail a search because the log cannot be written.
        }
    }

    private static void WriteList(Utf8JsonWriter writer, string name, IReadOnlyList<string>? values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values ?? [])
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }
}
