using System.Globalization;
using System.Text.RegularExpressions;
using AgentBrain.Application.Authorization;
using AgentBrain.Application.Writing;
using AgentBrain.Composition;
using AgentBrain.Domain.Entries;
using AgentBrain.Domain.Identity;
using AgentBrain.Domain.Metadata;
using AgentBrain.Infrastructure.Markdown.Frontmatter;

// Throwaway importer for the old manifest-based scaffold (A11). Reads <project>/.agent-memory and creates
// entries through the normal create operation, so validation and the secret scan apply. Rules are listed,
// not imported (they belong in the project's own instruction files). Nothing is truncated: entries that fail
// validation are reported for manual fixing. Delete this tool once every project is imported.
//
// usage: brain-legacy-import --source <path/to/.agent-memory> [--project <dir>] [--collection <id>] [--dry-run]

string? source = null;
string? project = null;
string? collection = null;
var dryRun = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--source": source = args[++i]; break;
        case "--project": project = args[++i]; break;
        case "--collection": collection = args[++i]; break;
        case "--dry-run": dryRun = true; break;
        default:
            Console.Error.WriteLine($"unknown argument {args[i]}");
            return 2;
    }
}

if (source is null || !Directory.Exists(source))
{
    Console.Error.WriteLine("usage: brain-legacy-import --source <path/to/.agent-memory> [--project <dir>] [--collection <id>] [--dry-run]");
    return 2;
}

var opened = await BrainRuntime.OpenAsync(new RuntimeOptions(Directory.GetCurrentDirectory()) { ProjectDirectory = project }, CancellationToken.None);
if (!opened.IsSuccess)
{
    Console.Error.WriteLine(opened.Error);
    return 2;
}

using var runtime = opened.Value;
CollectionId? target = collection is null ? null : CollectionId.Parse(collection);
var caller = new CallerContext(Environment.UserName, "legacy-import");
int imported = 0, failed = 0, skipped = 0;

foreach (var (path, entry) in Legacy.Read(source))
{
    if (entry is null)
    {
        skipped++;
        continue;
    }

    if (dryRun)
    {
        Console.WriteLine($"would import {WireNames.Kinds.Name(entry.Kind)} {entry.Id} from {path}");
        imported++;
        continue;
    }

    var result = await runtime.Maintenance.CreateAsync(caller, target, entry, [], CancellationToken.None);
    if (result.IsSuccess)
    {
        Console.WriteLine($"imported {result.Value.Ref} from {path}");
        imported++;
    }
    else
    {
        Console.WriteLine($"FAILED {path}: {result.Error}");
        failed++;
    }
}

Console.WriteLine($"{imported} imported, {failed} failed (fix and re-run; existing IDs are reported, not overwritten), {skipped} skipped");
return failed > 0 ? 1 : 0;

internal static partial class Legacy
{
    public static IEnumerable<(string Path, NewEntry? Entry)> Read(string root)
    {
        foreach (var file in Files(root, "rules"))
        {
            Console.WriteLine($"rule (not imported; move into your project instructions): {file}");
            yield return (file, null);
        }

        foreach (var file in Files(root, "memories"))
        {
            yield return (file, Convert(file, EntryKind.Memory));
        }

        foreach (var file in Files(root, "uncertainties"))
        {
            yield return (file, Convert(file, EntryKind.Uncertainty));
        }

        foreach (var file in Files(root, "reports"))
        {
            yield return (file, Convert(file, EntryKind.Report));
        }

        var pending = Path.Combine(root, "notes", "pending.md");
        if (File.Exists(pending))
        {
            foreach (var note in PendingNotes(File.ReadAllText(pending)))
            {
                yield return (pending, note);
            }
        }
    }

    private static IEnumerable<string> Files(string root, string folder)
    {
        var directory = Path.Combine(root, folder);
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.md").Where(f => !Path.GetFileName(f).Equals("README.md", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal)
            : [];
    }

    private static NewEntry? Convert(string path, EntryKind kind)
    {
        var (document, error) = FrontmatterParser.Parse(File.ReadAllText(path));
        if (document is null)
        {
            Console.WriteLine($"skipped {path}: {error!.Message}");
            return null;
        }

        var fields = document.Metadata.Entries.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
        var status = Text(fields, "status");
        if (status is "archived" or "superseded" or "resolved")
        {
            Console.WriteLine($"skipped {path}: status {status} (retired content is not imported)");
            return null;
        }

        var (title, body) = SplitTitle(document.Body, Path.GetFileNameWithoutExtension(path));
        var summary = Text(fields, "summary") ?? Text(fields, "task") ?? title;
        var evidence = new List<string>();
        if (Text(fields, "last_verified") is { } verifiedOn)
        {
            evidence.Add($"verified {verifiedOn} by {Text(fields, "verified_by") ?? "unknown"} (imported)");
        }

        return new NewEntry(kind, title, summary, body)
        {
            Id = EntryId.FromTitle(Path.GetFileNameWithoutExtension(path).Replace('_', '-')),
            Tags = List(fields, "tags"),
            Files = List(fields, "files").Concat(List(fields, "files_changed")).ToList(),
            Evidence = evidence,
            Blocking = kind == EntryKind.Uncertainty && Text(fields, "blocking") != "false",
        };
    }

    private static IEnumerable<NewEntry> PendingNotes(string text)
    {
        foreach (Match match in NoteHeading().Matches(text))
        {
            var title = match.Groups["title"].Value.Trim();
            var start = match.Index + match.Length;
            var next = NoteHeading().Match(text, start);
            var body = text[start..(next.Success ? next.Index : text.Length)].Trim();
            // The first bullet is the note itself; "Source:" and section labels are template scaffolding.
            var summary = body.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("- ", StringComparison.Ordinal)).Select(l => l[2..].Trim()).FirstOrDefault() ?? title;
            yield return new NewEntry(EntryKind.Note, title, summary.Length > 200 ? summary[..200] : summary, body)
            {
                Evidence = [$"pending note from {match.Groups["date"].Value} (imported)"],
            };
        }
    }

    private static (string Title, string Body) SplitTitle(string body, string fallback)
    {
        var lines = body.ReplaceLineEndings("\n").Split('\n').ToList();
        var index = lines.FindIndex(l => l.StartsWith("# ", StringComparison.Ordinal));
        if (index < 0)
        {
            return (fallback.Replace('-', ' ').Replace('_', ' '), body);
        }

        var title = lines[index][2..].Trim();
        foreach (var prefix in new[] { "Uncertainty:", "Report:" })
        {
            if (title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                title = title[prefix.Length..].Trim();
            }
        }

        lines.RemoveAt(index);
        return (title, string.Join('\n', lines).Trim() + "\n");
    }

    private static string? Text(Dictionary<string, MetadataNode> fields, string key) =>
        fields.TryGetValue(key, out var node) && node is MetadataScalar scalar ? scalar.Value : null;

    private static List<string> List(Dictionary<string, MetadataNode> fields, string key) =>
        fields.TryGetValue(key, out var node) && node is MetadataSequence sequence
            ? [.. sequence.Items.OfType<MetadataScalar>().Select(s => s.Value).Where(v => v.Length > 0)]
            : [];

    [GeneratedRegex(@"^## (?<date>\d{4}-\d{2}-\d{2})\s*-\s*(?<title>.+)$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex NoteHeading();
}
