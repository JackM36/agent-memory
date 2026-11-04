using System.Text;
using System.Text.Json;
using AgentBrain.Application.Decisions;

namespace AgentBrain.Infrastructure.Jev;

/// <summary>
/// The Jev wire format (POST /v1/systemone), written and read with <see cref="Utf8JsonWriter"/> and
/// <see cref="JsonDocument"/> so there is no reflection and the output is canonical: questions are sorted by name,
/// which makes the same request serialise to the same bytes (the cache key depends on that).
/// </summary>
internal static class DecisionWire
{
    public static string WriteRequest(DecisionRequest request, string model)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("model", model);
            writer.WriteString("state", request.State);
            writer.WriteStartObject("questions");
            foreach (var (name, question) in request.Questions.OrderBy(q => q.Key, StringComparer.Ordinal))
            {
                writer.WriteStartObject(name);
                switch (question)
                {
                    case ChoiceQuestion choice:
                        writer.WriteString("type", "choice");
                        writer.WriteString("instructions", choice.Instructions);
                        writer.WriteStartObject("criteria");
                        foreach (var (option, description) in choice.Options)
                        {
                            writer.WriteString(option, description);
                        }

                        writer.WriteEndObject();
                        break;
                    case ScoreQuestion score:
                        writer.WriteString("type", "score");
                        writer.WriteString("instructions", score.Instructions);
                        writer.WriteStartArray("criteria");
                        foreach (var level in score.Levels)
                        {
                            writer.WriteStringValue(level);
                        }

                        writer.WriteEndArray();
                        break;
                    default:
                        writer.WriteString("type", "noul");
                        writer.WriteString("instructions", question.Instructions);
                        break;
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Parses a response, or returns null when it is not the documented shape or lacks an answer to a question asked.</summary>
    public static DecisionResponse? ReadResponse(string json, DecisionRequest request)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("answers", out var answers))
            {
                return null;
            }

            var parsed = ReadAnswers(answers);
            if (parsed is null || request.Questions.Keys.Any(name => !parsed.ContainsKey(name)))
            {
                return null;
            }

            var tokens = root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object
                && usage.TryGetProperty("input_tokens", out var input) && input.TryGetInt32(out var count) ? count : 0;
            return new DecisionResponse(parsed, tokens);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public static Dictionary<string, DecisionAnswer>? ReadAnswers(JsonElement answers)
    {
        if (answers.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var result = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal);
        foreach (var property in answers.EnumerateObject())
        {
            var answer = property.Value;
            if (answer.ValueKind != JsonValueKind.Object || !answer.TryGetProperty("type", out var type))
            {
                return null;
            }

            switch (type.GetString())
            {
                case "noul":
                    if (!answer.TryGetProperty("noul", out var noul) || noul.ValueKind != JsonValueKind.Number)
                    {
                        return null;
                    }

                    result[property.Name] = new NoulAnswer(noul.GetDouble());
                    break;
                case "choice":
                    if (!answer.TryGetProperty("choice", out var choice) || choice.ValueKind != JsonValueKind.String)
                    {
                        return null;
                    }

                    var probabilities = new Dictionary<string, double>(StringComparer.Ordinal);
                    if (answer.TryGetProperty("probabilities", out var map) && map.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var entry in map.EnumerateObject().Where(e => e.Value.ValueKind == JsonValueKind.Number))
                        {
                            probabilities[entry.Name] = entry.Value.GetDouble();
                        }
                    }

                    result[property.Name] = new ChoiceAnswer(choice.GetString()!, Confidence(answer), probabilities);
                    break;
                case "score":
                    if (!answer.TryGetProperty("score", out var score) || score.ValueKind != JsonValueKind.Number)
                    {
                        return null;
                    }

                    result[property.Name] = new ScoreAnswer(score.GetDouble(), Confidence(answer));
                    break;
                default:
                    return null;
            }
        }

        return result;
    }

    /// <summary>Writes answers in the same shape the service returns them, for the cache file.</summary>
    public static void WriteAnswers(Utf8JsonWriter writer, IReadOnlyDictionary<string, DecisionAnswer> answers)
    {
        writer.WriteStartObject();
        foreach (var (name, answer) in answers.OrderBy(a => a.Key, StringComparer.Ordinal))
        {
            writer.WriteStartObject(name);
            switch (answer)
            {
                case NoulAnswer noul:
                    writer.WriteString("type", "noul");
                    writer.WriteNumber("noul", noul.Value);
                    break;
                case ChoiceAnswer choice:
                    writer.WriteString("type", "choice");
                    writer.WriteString("choice", choice.Choice);
                    writer.WriteNumber("confidence", choice.Confidence);
                    writer.WriteStartObject("probabilities");
                    foreach (var (option, p) in choice.Probabilities.OrderBy(p => p.Key, StringComparer.Ordinal))
                    {
                        writer.WriteNumber(option, p);
                    }

                    writer.WriteEndObject();
                    break;
                case ScoreAnswer score:
                    writer.WriteString("type", "score");
                    writer.WriteNumber("score", score.Score);
                    writer.WriteNumber("confidence", score.Confidence);
                    break;
            }

            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static double Confidence(JsonElement answer) =>
        answer.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : 0;
}
