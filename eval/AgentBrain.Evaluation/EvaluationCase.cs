using System.Text.Json;

namespace AgentBrain.Evaluation;

/// <summary>Expectations of one case or variant (eval/README.md).</summary>
internal sealed record Expectation(
    IReadOnlyList<string> Relevant,
    IReadOnlyList<string> Irrelevant,
    IReadOnlyList<string> Blocking,
    IReadOnlyList<string> NotBlocking,
    IReadOnlyList<string> Conflicts,
    int? MaxOccurrences,
    string? First,
    bool? Empty);

internal sealed record Variant(string? Relaxation, Expectation Expect);

internal sealed record EvaluationCase(
    string Id,
    IReadOnlyList<string> Categories,
    int Phase,
    string Query,
    string Context,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Symbols,
    bool IncludeHistory,
    bool IncludeIncompatible,
    IReadOnlyList<string>? Kinds,
    IReadOnlyList<string> Sources,
    IReadOnlyList<Variant> Variants);

/// <summary>A named query context from contexts.json.</summary>
internal sealed record EvaluationContext(string Name, string Project, IReadOnlyList<string> Technologies, IReadOnlyDictionary<string, string> Packages);

/// <summary>Reads cases and contexts with JsonDocument (no reflection; the runner shares the product's AOT constraints).</summary>
internal static class CaseLoader
{
    public static IReadOnlyList<EvaluationContext> LoadContexts(string casesDirectory)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(casesDirectory, "contexts.json")));
        return
        [
            .. document.RootElement.GetProperty("contexts").EnumerateObject().Select(p => new EvaluationContext(
                p.Name,
                p.Value.GetProperty("project").GetString()!,
                Strings(p.Value, "technologies"),
                p.Value.TryGetProperty("packages", out var packages)
                    ? packages.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetString()!, StringComparer.Ordinal)
                    : new Dictionary<string, string>())),
        ];
    }

    public static IReadOnlyList<EvaluationCase> LoadCases(string casesDirectory)
    {
        var cases = new List<EvaluationCase>();
        foreach (var file in Directory.EnumerateFiles(casesDirectory, "*.json").Where(f => Path.GetFileName(f) != "contexts.json").Order(StringComparer.Ordinal))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var c in document.RootElement.GetProperty("cases").EnumerateArray())
            {
                var include = c.TryGetProperty("include", out var inc) ? inc : default;
                var variants = c.TryGetProperty("variants", out var vs)
                    ? vs.EnumerateArray().Select(v => new Variant(OptionalString(v, "relaxation"), Expect(v.GetProperty("expect")))).ToList()
                    : [new Variant(OptionalString(c, "relaxation"), Expect(c.GetProperty("expect")))];
                cases.Add(new EvaluationCase(
                    c.GetProperty("id").GetString()!,
                    Strings(c, "categories"),
                    c.TryGetProperty("phase", out var phase) ? phase.GetInt32() : 2,
                    c.GetProperty("query").GetString()!,
                    c.GetProperty("context").GetString()!,
                    Strings(c, "files"),
                    Strings(c, "symbols"),
                    include.ValueKind == JsonValueKind.Object && include.TryGetProperty("history", out var h) && h.GetBoolean(),
                    include.ValueKind == JsonValueKind.Object && include.TryGetProperty("incompatible", out var i) && i.GetBoolean(),
                    c.TryGetProperty("filters", out var filters) && filters.TryGetProperty("kinds", out _) ? Strings(filters, "kinds") : null,
                    c.TryGetProperty("sources", out _) ? Strings(c, "sources") : ["corpus"],
                    variants));
            }
        }

        return cases;
    }

    private static Expectation Expect(JsonElement e) => new(
        Strings(e, "relevant"),
        Strings(e, "irrelevant"),
        Strings(e, "blocking"),
        Strings(e, "notBlocking"),
        Strings(e, "conflicts"),
        e.TryGetProperty("maxOccurrences", out var max) ? max.GetInt32() : null,
        e.TryGetProperty("first", out var first) ? first.GetString() : null,
        e.TryGetProperty("empty", out var empty) ? empty.GetBoolean() : null);

    private static List<string> Strings(JsonElement element, string name) =>
        element.TryGetProperty(name, out var array) ? [.. array.EnumerateArray().Select(v => v.GetString()!)] : [];

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.GetString() : null;
}
