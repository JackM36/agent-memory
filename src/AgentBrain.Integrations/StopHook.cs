using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentBrain.Integrations;

/// <summary>The hook's answer: null to let the agent stop, or a reason that blocks the stop once.</summary>
public sealed record StopDecision(string? BlockReason);

/// <summary>
/// Stop-hook logic shared by Claude Code and Codex (their Stop contracts are the same: JSON on stdin with
/// <c>transcript_path</c> and <c>stop_hook_active</c>; <c>{"decision":"block","reason":…}</c> on stdout blocks
/// once). A C# port of the old <c>report-nudge.sh</c>: when the session edited project files but saved nothing
/// to Agent Brain, nudge once. Edits inside <c>.agent-brain/</c> do not count as project edits.
/// </summary>
public static partial class StopHook
{
    public static StopDecision Evaluate(string hookInput, Func<string, string?> readTranscript)
    {
        ArgumentNullException.ThrowIfNull(readTranscript);
        string? transcriptPath;
        try
        {
            using var input = JsonDocument.Parse(hookInput);
            var root = input.RootElement;
            if (root.TryGetProperty("stop_hook_active", out var active) && active.ValueKind == JsonValueKind.True)
            {
                return new StopDecision(null);
            }

            transcriptPath = root.TryGetProperty("transcript_path", out var path) ? path.GetString() : null;
        }
        catch (JsonException)
        {
            return new StopDecision(null);
        }

        var transcript = transcriptPath is null ? null : readTranscript(transcriptPath);
        if (transcript is null)
        {
            return new StopDecision(null);
        }

        var edited = EditedPaths(transcript).Select(p => p.Replace("\\\\", "/", StringComparison.Ordinal).Replace('\\', '/')).ToList();
        var projectEdits = edited.Where(p => !p.Contains(".agent-brain/", StringComparison.Ordinal)).ToList();
        if (projectEdits.Count == 0 || Captured().IsMatch(transcript))
        {
            return new StopDecision(null);
        }

        return new StopDecision(Templates.StopHookReason);
    }

    /// <summary>The JSON a host expects on stdout to block the stop; empty when the agent may stop.</summary>
    public static string Output(StopDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (decision.BlockReason is null)
        {
            return "";
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("decision", "block");
            writer.WriteString("reason", decision.BlockReason);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Paths the session actually edited (not merely read or mentioned).</summary>
    public static IEnumerable<string> EditedPaths(string transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        foreach (Match match in ClaudeEdit().Matches(transcript))
        {
            yield return match.Groups["path"].Value;
        }

        foreach (Match match in CodexPatch().Matches(transcript))
        {
            yield return match.Groups["path"].Value;
        }
    }

    // Claude Code: tool_use blocks named Edit/Write/MultiEdit, file_path directly inside their input.
    [GeneratedRegex(@"""name""\s*:\s*""(?:Edit|Write|MultiEdit|NotebookEdit)""\s*,\s*""input""\s*:\s*\{\s*""(?:file_path|notebook_path)""\s*:\s*""(?<path>[^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex ClaudeEdit();

    // Codex: apply_patch hunks; the path ends at the JSON string escape for the newline.
    [GeneratedRegex(@"\*\*\* (?:Add|Update|Delete) File: (?<path>[^""\\]+)", RegexOptions.CultureInvariant)]
    private static partial Regex CodexPatch();

    // A capture happened: an Agent Brain write tool call, or a brain write command.
    [GeneratedRegex(@"mcp__agent-brain__(?:save|propose|publish)|""name""\s*:\s*""(?:save|propose|publish)""|\bbrain (?:new|update|archive|supersede|resolve|propose|publish)\b", RegexOptions.CultureInvariant)]
    private static partial Regex Captured();
}
