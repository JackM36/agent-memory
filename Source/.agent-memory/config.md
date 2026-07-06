# Agent Memory Config

## Defaults

- Memory directory: `.agent-memory/memories/`
- Rule directory: `.agent-memory/rules/`
- Uncertainty directory: `.agent-memory/uncertainties/`
- Report directory: `.agent-memory/reports/`
- Notes directory: `.agent-memory/notes/`
- Archive directory: `.agent-memory/archive/`
- Template directory: `.agent-memory/templates/`
- Command workflow directory: `.agent-memory/commands/`
- Agent specification directory: `.agent-memory/agents/`
- Tool directory: `.agent-memory/tools/`
- Hook directory: `.agent-memory/hooks/` (shared Stop-hook script + config fragments)

## Native Wrapper Directories

- Claude wrappers: `.claude/`
- Codex wrappers: `.agents/skills/` and `.codex/agents/`
- Gemini wrappers: `.gemini/`
- OpenCode wrappers: `.opencode/`

## Install Defaults

- Root installer path in source repository: `tools/install.js`
- Root uninstaller path in source repository: `tools/uninstall.js`
- Target-project uninstaller: `.agent-memory/tools/uninstall-project-memory.js`
- Target-project interactive uninstaller: `.agent-memory/tools/uninstall-project-memory-interactive.js`
- Target-project init helper: `.agent-memory/tools/init-project-memory.js`
- Default installer integrations: none
- Default `CLAUDE.md` behavior: managed reference to `AGENTS.md`
- Installer overwrite behavior: refuse changed target files unless `--force`
- Stop-hook wiring: enabled by default for selected `claude` / `codex` integrations; `--no-hooks` skips it
- Stop-hook config targets: `.claude/settings.local.json` (Claude), `.codex/hooks.json` (Codex)
- Default uninstaller mode: `keep-data`
- Uninstaller modes: `keep-data`, `delete-all`

## Size Targets

- Manifest target: under 200 lines.
- Rule target: 20-100 lines.
- Memory target: 50-150 lines.
- Memory split threshold: 250 lines.
- Report target: no strict limit, but keep structured.

## Behavior

- Archive resolved uncertainties: true
- Ask before acting on blocking uncertainties: true
- Reports are read by default: false
- Manifest is read by default for behavior-changing, unfamiliar, file/path-driven, historical, or decision-sensitive work: true
- Always-read rules are read after manifest: true
- Markdown is the source of truth: true
- Generated JSON is optional and derived: true
- Native wrappers delegate to generic `.agent-memory/commands/` and `.agent-memory/agents/`: true

## Visibility

- Announce memory reads: true
- Announce memory writes: true
- Announce memory-scout use: true
- Keep memory announcements concise: true
- Reveal private reasoning or hidden chain-of-thought: false

## Enforcement Hooks

- Shared Stop-hook script: `.agent-memory/hooks/report-nudge.sh`. If a session edits project files (outside `.agent-memory/`) but writes no report, it blocks the stop once and asks for a report; small/very specific/trivial edits may be skipped.
- Config fragments merged into the host: `.agent-memory/hooks/fragments/claude-settings.hooks.json`, `.agent-memory/hooks/fragments/codex-hooks.json`.
- Only Claude Code and Codex CLI support a pre-stop hook. OpenCode and Gemini rely on the `AGENTS.md` workflow wording only.
- Requires `bash` on the host (e.g. Git Bash on Windows).

## Agent-Specific Notes

- OpenCode does not hot-reload command, agent, skill, or config files. Restart OpenCode after changing `.opencode/` files.
- Codex uses the included `$agent-memory` skill and `.codex/agents/memory-scout.toml`. Codex custom slash-command files are not included because the current Codex docs describe built-in slash commands, skills, and custom agents, not repo-scoped custom slash command files.

## Token Budgets

- Default memory query budget: 1500 tokens.
- Default report query budget: 3000 tokens.
- Prefer compact manifest entries before full file reads.

## Data Format

- Author rules, memories, uncertainties, reports, and pending notes in Markdown.
- Use generated JSON only for tooling, search indexes, dashboards, or visualization.
- Do not hand-edit generated JSON as source data.

## Naming

- Use lowercase kebab-case filenames.
- Use `YYYY-MM-DD_HHMM_short-name.md` for reports.

## Frontmatter

- Memories require: `id`, `type`, `tags`, `files`, `summary`, `updated`, `last_verified`, `verified_by`, `confidence`, `status`.
- Rules require: `id`, `type`, `tags`, `files`, `summary`, `always_read`, `priority`, `updated`, `status`.
- Uncertainties require: `id`, `type`, `tags`, `files`, `summary`, `blocking`, `created`, `status`.
- Reports require: `id`, `type`, `date`, `agent`, `task`, `tags`, `files_changed`, `memory_updates`, `uncertainties_created`, `uncertainties_resolved`, `read_when`.
