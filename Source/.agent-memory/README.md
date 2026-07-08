# Agent Memory

Repository-local, agent-agnostic memory for durable project context.

The operating protocol lives in `protocol.md` — that file is the single source of truth for how
agents use this system.

## Mental Model

```txt
manifest.md       = router / table of contents (generated indexes)
protocol.md       = the operating protocol (single source of truth)
rules/            = active instructions agents should follow
memories/         = compact resolved project knowledge
uncertainties/    = unresolved questions that prevent unsafe assumptions
reports/          = detailed chronological audit trail (index in reports/index.md)
notes/            = temporary uncurated pending notes
archive/          = old or resolved context
templates/        = canonical file templates
commands/         = generic command workflows
agents/           = generic agent specifications
hooks/            = shared Stop-hook script + config fragments
tools/            = maintenance scripts (refresh-manifest, validate, new-*)
```

## Creating Files

Use the canonical templates in `templates/` when creating rules, memories, uncertainties, reports,
or pending notes, then regenerate the indexes:

```sh
node .agent-memory/tools/refresh-manifest.js
```

Helper scripts (`new-rule.js`, `new-memory.js`, `new-uncertainty.js`, `new-report.js`) scaffold new
entries. Validate after structural changes:

```sh
node .agent-memory/tools/validate-memory.js
```

Initialize or update project instruction files after manual copying (idempotent, manages marker
blocks in `AGENTS.md` and `CLAUDE.md`):

```sh
node .agent-memory/tools/init-project-memory.js
```

## Uninstall

Run from an installed project:

```sh
node .agent-memory/tools/uninstall-project-memory-interactive.js
```

Non-interactive: `uninstall-project-memory.js --mode keep-data` removes the memory system and
native wrappers but keeps project-created data folders; `--mode delete-all` removes the entire
`.agent-memory/` folder.

## Native Wrappers

Root-level wrappers exist for common agents and stay thin, pointing back to `commands/` and
`agents/`:

- `.claude/` for Claude commands, skill, memory-scout agent, and memory-maintainer agent.
- `.gemini/` for Gemini commands, memory-scout agent, and memory-maintainer agent.
- `.opencode/` for OpenCode commands, skill, memory-scout agent, and memory-maintainer agent.
- `.agents/skills/` and `.codex/agents/` for Codex skill, memory-scout agent, and memory-maintainer agent.

## Enforcement Hook

`hooks/report-nudge.sh` is a shared Stop hook. When a session edits project files (outside
`.agent-memory/`) but writes nothing under `.agent-memory/reports/`, it blocks the stop once and
asks for a report — small, very specific, or trivial edits may be skipped. The installer wires it
into `.claude/settings.json` and `.codex/hooks.json` for the selected integrations (skip with
`--no-hooks`). Only Claude Code and Codex support a pre-stop hook; OpenCode and Gemini rely on the
`AGENTS.md` workflow wording. Requires `bash` on the host (Git Bash on Windows).
