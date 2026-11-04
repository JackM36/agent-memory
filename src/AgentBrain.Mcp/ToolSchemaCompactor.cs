using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace AgentBrain.Mcp;

/// <summary>
/// Shrinks generated tool input schemas (plan §8, tool-definition budget). Optional parameters come out of the
/// SDK as <c>"type": ["string", "null"], "default": null</c>; for a model reading the definitions, "not required"
/// already says the same thing. Nothing else changes: names, descriptions and required lists stay as written.
/// </summary>
internal static class ToolSchemaCompactor
{
    public static void Compact(McpServerOptions options)
    {
        foreach (var tool in options.ToolCollection ?? [])
        {
            var schema = JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText())!;
            Simplify(schema);
            using var document = JsonDocument.Parse(schema.ToJsonString());
            tool.ProtocolTool.InputSchema = document.RootElement.Clone();
        }
    }

    internal static void Simplify(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["type"] is JsonArray types && types.Count == 2 && types.Any(t => t?.GetValue<string>() == "null"))
                {
                    obj["type"] = types.First(t => t?.GetValue<string>() != "null")!.GetValue<string>();
                }

                if (obj.TryGetPropertyValue("default", out var value) && IsEmptyDefault(value))
                {
                    obj.Remove("default");
                }

                foreach (var (_, child) in obj.ToList())
                {
                    if (child is not null)
                    {
                        Simplify(child);
                    }
                }

                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    if (child is not null)
                    {
                        Simplify(child);
                    }
                }

                break;
        }
    }

    private static bool IsEmptyDefault(JsonNode? value) =>
        value is null || (value is JsonValue v && v.GetValueKind() == JsonValueKind.False);
}
