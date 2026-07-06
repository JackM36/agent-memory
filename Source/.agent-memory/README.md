# Agent Memory

Repository-local, agent-agnostic memory for durable project context.

## Mental Model

```txt
manifest.md       = router / table of contents
rules/            = active instructions agents should follow
memories/         = compact resolved project knowledge
uncertainties/    = unresolved questions that prevent unsafe assumptions
reports/          = detailed chronological audit trail
notes/            = temporary uncurated pending notes
archive/          = old or resolved context
templates/        = canonical file templates
commands/         = generic command workflows
agents/           = generic agent specifications
hooks/            = shared Stop-hook script + config fragments
tools/            = optional maintenance scripts
```

## Core Rule

Agents read the manifest for behavior-changing, unfamiliar, file/path-driven, historical, or decision-sensitive work. They read always-read rules, sometimes read task-relevant rules and memories, check relevant uncertainties, and rarely read reports. For non-trivial work, agents may use `memory-scout` before implementation and `memory-maintainer` after implementation.

## Creating Files

Use the canonical templates in `.agent-memory/templates/` when creating new rules, memories, uncertainties, reports, or pending notes.

Optional helper scripts are available in `.agent-memory/tools/`.

Initialize or update project instruction files after manual copying:

```sh
node .agent-memory/tools/init-project-memory.js
```

The init helper manages marker blocks in `AGENTS.md` and `CLAUDE.md`, so it can be run repeatedly.

## Uninstall

Run from an installed project:

```sh
node .agent-memory/tools/uninstall-project-memory-interactive.js
```

Non-interactive modes:

```sh
node .agent-memory/tools/uninstall-project-memory.js --mode keep-data
node .agent-memory/tools/uninstall-project-memory.js --mode delete-all
```

`keep-data` removes the memory system and native wrappers but keeps project-created rules, memories, uncertainties, reports, pending notes, and archive files. `delete-all` removes the entire `.agent-memory/` folder.

## Native Wrappers

The copied scaffold also includes root-level wrappers for common agents:

- `.claude/` for Claude commands, skill, memory-scout agent, and memory-maintainer agent.
- `.gemini/` for Gemini commands, memory-scout agent, and memory-maintainer agent.
- `.opencode/` for OpenCode commands, skill, memory-scout agent, and memory-maintainer agent.
- `.agents/skills/` and `.codex/agents/` for Codex skill, memory-scout agent, and memory-maintainer agent.

These wrappers should stay thin and point back to `.agent-memory/commands/` and `.agent-memory/agents/`.

## Enforcement Hook

`.agent-memory/hooks/report-nudge.sh` is a shared Stop hook. When a session edits project files
(outside `.agent-memory/`) but writes no report, it blocks the stop once and asks for a report —
small, very specific, or trivial edits may be skipped. The installer wires it into
`.claude/settings.local.json` and `.codex/hooks.json` for the selected integrations (skip with
`--no-hooks`). Only Claude Code and Codex support a pre-stop hook; OpenCode and Gemini rely on the
`AGENTS.md` workflow wording. Requires `bash` on the host (Git Bash on Windows).
