using System.ComponentModel;
using AgentBrain.Composition;
using ModelContextProtocol.Server;

namespace AgentBrain.Mcp;

/// <summary>
/// MCP prompts: work the user starts on purpose, never automatically. <c>promote</c> asks the agent to review project
/// memory for lessons worth keeping as general knowledge. Agent Brain makes no model calls, so generalising the text
/// is the agent's job; saving goes through the usual <c>save</c> routing, so the knowledge collection's policy
/// (direct or proposal) still decides what lands.
/// </summary>
[McpServerPromptType]
public sealed class BrainPrompts(BrainRuntime runtime)
{
    [McpServerPrompt(Name = "promote", Title = "Promote general lessons")]
    [Description("Review this project's memory for lessons that would help in other projects and save general versions as knowledge.")]
    public string Promote()
    {
        var configuration = runtime.Configuration;
        var memory = configuration.MemoryCollection?.Value ?? "the project memory collection";
        var knowledge = configuration.KnowledgeCollection?.Value;
        var destination = knowledge is null
            ? "No knowledge collection is configured (writes.knowledge). Tell the user, suggest `brain collection create library --knowledge`, and stop."
            : $"General knowledge goes to `{knowledge}`; `save` with kind `knowledge` sends it there.";

        return $"""
            Review the entries in `{memory}` for lessons that would help in other projects.

            {destination}

            1. List the project memory (`list` with kinds `memory`; keep the references that start with `{memory}/`),
               then `get` the candidates.
            2. Keep only lessons that stay true outside this project: library or engine behaviour, a pitfall, a practice
               that proved itself. Skip decisions, layouts and facts about this codebase.
            3. For each, `search` first: if the knowledge collection already covers it, update that entry instead.
            4. Write a general version: no project names, paths, class names or ticket numbers. Keep the reasoning and
               the evidence. Set `technologies` when it only applies to some (for example unity).
            5. `save` it with kind `knowledge` and evidence `promoted from <collection/entry>`. Leave the project
               entry in place: it keeps the concrete details.
            6. Finish with one line per entry: saved, proposed (awaiting review), or skipped and why.

            Do not save anything you are unsure is general; list it as a question for the user instead.
            """;
    }
}
