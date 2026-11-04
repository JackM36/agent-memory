using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBrain.Application.Common;
using AgentBrain.Application.Decisions;

namespace AgentBrain.Infrastructure.Jev;

/// <summary>
/// Remembers answers by SHA-256 of (model, canonical request), in memory and optionally in an append-only JSONL
/// file, so a repeated question costs nothing and a run can be replayed. The file holds only the key and the answers,
/// never the state text. Only successful answers are cached. A bad or unwritable file degrades to memory only.
/// </summary>
public sealed class CachingDecisionModel : IDecisionModel
{
    private readonly IDecisionModel _inner;
    private readonly string _model;
    private readonly string? _cacheFile;
    private readonly ConcurrentDictionary<string, DecisionResponse> _entries = new(StringComparer.Ordinal);
    private readonly Lock _fileLock = new();

    public CachingDecisionModel(IDecisionModel inner, string model, string? cacheFile)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        _inner = inner;
        _model = model;
        _cacheFile = string.IsNullOrWhiteSpace(cacheFile) ? null : cacheFile;
        Load();
    }

    public async ValueTask<Result<DecisionResponse>> AskAsync(DecisionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = Key(request);
        if (_entries.TryGetValue(key, out var hit))
        {
            return hit with { InputTokens = 0, FromCache = true };
        }

        var result = await _inner.AskAsync(request, cancellationToken);
        if (result.IsSuccess)
        {
            if (_entries.TryAdd(key, result.Value))
            {
                Append(key, result.Value);
            }
        }

        return result;
    }

    private string Key(DecisionRequest request) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(_model + "\n" + DecisionWire.WriteRequest(request, _model))));

    private void Load()
    {
        if (_cacheFile is null || !File.Exists(_cacheFile))
        {
            return;
        }

        try
        {
            foreach (var line in File.ReadLines(_cacheFile))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (root.TryGetProperty("k", out var key) && key.GetString() is { Length: > 0 } k
                        && root.TryGetProperty("a", out var answers) && DecisionWire.ReadAnswers(answers) is { } parsed)
                    {
                        _entries[k] = new DecisionResponse(parsed, 0);
                    }
                }
                catch (JsonException)
                {
                    // A torn or corrupt line is skipped; the rest of the file is still good.
                }
            }
        }
        catch (IOException)
        {
            // The cache is an optimisation.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void Append(string key, DecisionResponse response)
    {
        if (_cacheFile is null)
        {
            return;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("k", key);
            writer.WritePropertyName("a");
            DecisionWire.WriteAnswers(writer, response.Answers);
            writer.WriteEndObject();
        }

        var line = Encoding.UTF8.GetString(stream.ToArray()) + "\n";
        try
        {
            lock (_fileLock)
            {
                if (Path.GetDirectoryName(Path.GetFullPath(_cacheFile)) is { } directory)
                {
                    Directory.CreateDirectory(directory);
                }

                File.AppendAllText(_cacheFile, line, Encoding.UTF8);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
