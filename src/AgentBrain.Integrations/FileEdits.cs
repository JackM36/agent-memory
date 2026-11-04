using System.Text.Json;
using System.Text.Json.Nodes;
using Tomlyn.Parsing;

namespace AgentBrain.Integrations;

public enum FileAction
{
    Created,
    Updated,
    Unchanged,
    Skipped,
}

/// <summary>What <c>init</c> did to one file.</summary>
public sealed record FileChange(string Path, FileAction Action, string? Detail = null);

/// <summary>
/// Safe edits to files other tools also own (plan §9.4): JSON and TOML are merged, never overwritten;
/// unrelated content is preserved; a one-time backup is written before the first modification.
/// </summary>
public static class FileEdits
{
    public const string BackupSuffix = ".agent-brain.bak";



    public static FileChange WriteIfMissing(string path, string content)
    {
        if (File.Exists(path))
        {
            return new FileChange(path, FileAction.Unchanged, "exists");
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return new FileChange(path, FileAction.Created);
    }

    /// <summary>Writes a file Agent Brain owns completely (skills), replacing older versions.</summary>
    public static FileChange WriteOwned(string path, string content)
    {
        if (File.Exists(path) && File.ReadAllText(path) == content)
        {
            return new FileChange(path, FileAction.Unchanged);
        }

        var existed = File.Exists(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return new FileChange(path, existed ? FileAction.Updated : FileAction.Created);
    }

    /// <summary>Inserts or replaces the marker-delimited section; text outside the markers is untouched.</summary>
    public static FileChange UpsertSection(string path, string section, string begin, string end)
    {
        var existing = File.Exists(path) ? File.ReadAllText(path) : null;
        string updated;
        if (existing is null)
        {
            updated = section.TrimEnd() + "\n";
        }
        else
        {
            // Match any version of the section: compare up to and including ":begin".
            var versionless = begin[..(begin.IndexOf(":begin", StringComparison.Ordinal) + ":begin".Length)];
            var start = existing.IndexOf(versionless, StringComparison.Ordinal);
            var stop = existing.IndexOf(end, StringComparison.Ordinal);
            updated = start >= 0 && stop > start
                ? existing[..start] + section.TrimEnd() + existing[(stop + end.Length)..]
                : existing.TrimEnd() + "\n\n" + section.TrimEnd() + "\n";
        }

        return Save(path, existing, updated);
    }

    /// <summary>Appends missing lines (e.g. .gitignore entries).</summary>
    public static FileChange EnsureLines(string path, IEnumerable<string> lines)
    {
        var existing = File.Exists(path) ? File.ReadAllText(path) : null;
        var present = (existing ?? "").Split('\n').Select(l => l.Trim()).ToHashSet(StringComparer.Ordinal);
        var missing = lines.Where(l => !present.Contains(l)).ToList();
        if (missing.Count == 0)
        {
            return new FileChange(path, FileAction.Unchanged);
        }

        var prefix = existing is null || existing.Length == 0 || existing.EndsWith('\n') ? existing ?? "" : existing + "\n";
        return Save(path, existing, prefix + "# Agent Brain\n" + string.Join('\n', missing) + "\n");
    }

    /// <summary>Merges <paramref name="patch"/> into a JSON file: objects merge recursively, other values are set.</summary>
    public static FileChange MergeJson(string path, JsonObject patch)
    {
        var existing = File.Exists(path) ? File.ReadAllText(path) : null;
        JsonObject root;
        try
        {
            root = existing is null ? [] : JsonNode.Parse(existing, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject ?? [];
        }
        catch (JsonException ex)
        {
            return new FileChange(path, FileAction.Skipped, $"not valid JSON ({ex.Message}); add the Agent Brain settings by hand");
        }

        Merge(root, patch);
        return Save(path, existing, Indent(root) + "\n");
    }

    private static string Indent(JsonNode node)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            node.WriteTo(writer);
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Adds a TOML table if no table with that name exists. Existing content is kept byte for byte.</summary>
    public static FileChange EnsureTomlTable(string path, string tableName, string tableText)
    {
        var existing = File.Exists(path) ? File.ReadAllText(path) : null;
        if (existing is not null)
        {
            var document = SyntaxParser.Parse(existing, path, false);
            if (document.HasErrors)
            {
                return new FileChange(path, FileAction.Skipped, "not valid TOML; add the Agent Brain table by hand");
            }

            if (document.Tables.Any(t => t.Name?.ToString().Trim() == tableName))
            {
                return new FileChange(path, FileAction.Unchanged);
            }
        }

        var prefix = existing is null ? "" : existing.TrimEnd() + "\n\n";
        return Save(path, existing, prefix + tableText.TrimEnd() + "\n");
    }

    private static void Merge(JsonObject target, JsonObject patch)
    {
        foreach (var (key, value) in patch)
        {
            if (value is JsonObject patchObject && target[key] is JsonObject targetObject)
            {
                Merge(targetObject, patchObject);
            }
            else if (value is JsonArray patchArray && target[key] is JsonArray targetArray)
            {
                foreach (var item in patchArray)
                {
                    var text = item?.ToJsonString();
                    if (!targetArray.Any(existing => existing?.ToJsonString() == text))
                    {
                        targetArray.Add(item?.DeepClone());
                    }
                }
            }
            else
            {
                target[key] = value?.DeepClone();
            }
        }
    }

    private static FileChange Save(string path, string? existing, string updated)
    {
        if (existing == updated)
        {
            return new FileChange(path, FileAction.Unchanged);
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        if (existing is not null && !File.Exists(path + BackupSuffix))
        {
            File.Copy(path, path + BackupSuffix);
        }

        File.WriteAllText(path, updated);
        return new FileChange(path, existing is null ? FileAction.Created : FileAction.Updated);
    }
}
