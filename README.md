# Agent Memory

Reusable, agent-agnostic project memory for coding agents.

Agent Memory gives a repository a small local memory system that survives across agent sessions. It stores durable project instructions, resolved knowledge, unresolved questions, task reports, and native agent wrappers in plain Markdown files.

The goal is simple: agents should not rediscover the same context every session, and they should not load the entire history just to make one change.

## Why Use This

- Keep project memory local to the repository.
- Share context across Claude, Codex, Gemini CLI, OpenCode, and Markdown-capable agents.
- Route agents through a compact manifest instead of broad rereads.
- Preserve instructions separately from facts and historical reports.
- Capture uncertainty explicitly so agents ask instead of guessing.
- Install and uninstall without package managers or runtime dependencies beyond Node.js.

## What Gets Installed

The core scaffold is copied into the target project:

```txt
.agent-memory/
```

Optional native integrations can also be installed:

```txt
.claude/      Claude commands, skill, memory-scout agent, and memory-maintainer agent
.gemini/      Gemini commands, memory-scout agent, and memory-maintainer agent
.opencode/    OpenCode commands, skill, memory-scout agent, and memory-maintainer agent
.agents/      Codex skill
.codex/       Codex memory-scout agent and memory-maintainer agent
```

Codex custom slash-command files are not included because current Codex docs describe built-in slash commands, skills, and custom agents, not repo-scoped custom slash-command files.

### Stop-hook enforcement

The scaffold ships a shared Stop-hook script at `.agent-memory/hooks/report-nudge.sh`. When a session
edits project files but writes no report to `.agent-memory/reports/`, the hook nudges the agent once
to write one (it explicitly allows skipping small/trivial edits). When the `claude` and/or `codex`
integrations are installed, the installer wires this hook into `.claude/settings.json` (the
project-shared settings file, so the hook travels with the repo) and `.codex/hooks.json` — merging
into an existing file (preserving other keys) or creating it if absent.
Pass `--no-hooks` to skip wiring. OpenCode and Gemini have no equivalent pre-stop hook, so they get
the workflow wording via `AGENTS.md` only.

## Requirements

- Node.js available as `node` on `PATH`.
- PowerShell for the Windows launcher.
- `osascript` for the macOS launcher.

The memory scaffold itself is Markdown-first. The Node scripts are optional helpers for install, validation, and maintenance.

## Quick Start

Clone or download this repository, then run the installer from the repository root.

Windows:

```powershell
.\install-windows.ps1
```

macOS:

```sh
./install-mac.sh
```

The interactive installer will ask for:

- Target project folder.
- Native integrations to install.
- `CLAUDE.md` behavior.

It then installs the scaffold, creates or updates `AGENTS.md`, and validates the target project.

## Scripted Install

Install only the core memory system:

```sh
node tools/install.js --target ../TargetProject --agents none
```

Install selected integrations:

```sh
node tools/install.js --target ../TargetProject --agents claude,opencode
```

Install every supported integration:

```sh
node tools/install.js --target ../TargetProject --agents all
```

Choose `CLAUDE.md` behavior:

```sh
node tools/install.js --target ../TargetProject --agents claude --claude reference
node tools/install.js --target ../TargetProject --agents claude --claude full
node tools/install.js --target ../TargetProject --agents claude --claude none
```

Installer behavior:

- Copies `.agent-memory/` into the target project.
- Copies only selected native integration folders.
- Adds or updates a managed Project Memory section in `AGENTS.md`.
- Creates `AGENTS.md` if it does not exist.
- Adds or updates a managed `CLAUDE.md` reference by default.
- Wires the Stop hook into `.claude/settings.json` / `.codex/hooks.json` for the selected
  integrations (idempotent, preserves other keys). Pass `--no-hooks` to skip.
- Refuses to overwrite changed target files unless `--force` is passed.

OpenCode does not hot-reload command, agent, skill, or config files. Restart OpenCode after installing or changing `.opencode/` files.

## Manual Install

Copy the contents of `Source/` into the target project root.

Then run this from the target project:

```sh
node .agent-memory/tools/init-project-memory.js
```

This creates or updates the managed Project Memory section in `AGENTS.md`. If `AGENTS.md` does not exist, it creates one with just that section.

## How Agents Use It

Installed projects get a compact `AGENTS.md` section that points to `.agent-memory/protocol.md`,
the single source of truth for the workflow. In short: agents route through the manifest (skipping
lookup entirely when it is empty or unrelated to the task), read only relevant rules, memories, and
uncertainties, and after work that created durable context they curate memories/rules and
regenerate the manifest — delegating to the `memory-scout` and `memory-maintainer` subagents when
that is proportionate. Memory upkeep is background housekeeping; it never appears as steps in task
plans presented to the user.

The key idea:

> Manifest routes context. Rules guide behavior. Memories store resolved knowledge. Uncertainties block unsafe assumptions. Reports preserve history.

## Memory Types

- `rules/` stores active project instructions agents should follow.
- `memories/` stores compact resolved project knowledge.
- `uncertainties/` stores unresolved questions and unsafe assumptions.
- `reports/` stores detailed chronological task history.
- `notes/` stores uncurated pending notes that could not be safely classified immediately.
- `archive/` stores resolved or obsolete material.

Markdown files are the source of truth. Generated JSON, dashboards, or indexes should be derived from Markdown rather than edited directly.

## Commands

Generic command workflows live in `.agent-memory/commands/`. Native wrappers call back to these workflows where supported.

Common commands:

- `/memory-init` initializes project instruction files after manual copy.
- `/memory-query <topic>` finds relevant memory context.
- `/memory-file-history <path>` finds memory related to a file or folder.
- `/memory-scout <task>` runs the read-only context scout workflow.
- `/memory-maintain <handoff>` runs post-work report and memory maintenance.
- `/memory-add <context>` adds user-provided project context.
- `/memory-refresh` curates reports and pending notes into durable memory.
- `/memory-prune` finds stale or duplicate memory entries.
- `/memory-resolve <uncertainty>` resolves an uncertainty using confirmed evidence.

These commands are conventions even when a host does not support native slash commands.

## Maintenance Tools

Run tools from the target project root:

```sh
node .agent-memory/tools/validate-memory.js
node .agent-memory/tools/init-project-memory.js
node .agent-memory/tools/new-rule.js coding-style
node .agent-memory/tools/new-memory.js architecture-overview
node .agent-memory/tools/new-uncertainty.js unclear-cache-lifetime
node .agent-memory/tools/new-report.js feature-name
node .agent-memory/tools/refresh-manifest.js
```

Validate after install or after structural edits:

```sh
node .agent-memory/tools/validate-memory.js
```

## Uninstall

Interactive uninstall from this repository:

Windows:

```powershell
.\uninstall-windows.ps1
```

macOS:

```sh
./uninstall-mac.sh
```

Interactive uninstall from inside an installed project:

```sh
node .agent-memory/tools/uninstall-project-memory-interactive.js
```

Scripted uninstall from this repository:

```sh
node tools/uninstall.js --target ../TargetProject --mode keep-data
```

Scripted uninstall from inside an installed project:

```sh
node .agent-memory/tools/uninstall-project-memory.js --mode keep-data
```

Modes:

- `keep-data` removes agent instruction sections, native wrappers, the Stop-hook entries from `.claude/settings.json` / `.codex/hooks.json` (plus legacy `.claude/settings.local.json` entries; other keys stay intact and a config file is deleted only if it becomes empty), and memory-system scaffold files, but keeps project-created data in `.agent-memory/rules/`, `.agent-memory/memories/`, `.agent-memory/uncertainties/`, `.agent-memory/reports/`, `.agent-memory/notes/`, and `.agent-memory/archive/`.
- `delete-all` removes agent instruction sections, native wrappers, the Stop-hook config entries, and the entire `.agent-memory/` folder.

Delete everything:

```sh
node tools/uninstall.js --target ../TargetProject --mode delete-all
```

## Repository Layout

```txt
Source/                         Copyable scaffold for target projects
  .agent-memory/                Core memory system
    hooks/                      Shared Stop-hook script + config fragments
  .claude/                      Claude wrappers
  .gemini/                      Gemini wrappers
  .opencode/                    OpenCode wrappers
  .agents/                      Codex skill wrapper
  .codex/                       Codex custom agent wrapper
tools/                          Source-repo install/uninstall scripts
install-windows.ps1             Windows interactive installer
install-mac.sh                  macOS interactive installer
uninstall-windows.ps1           Windows interactive uninstaller
uninstall-mac.sh                macOS interactive uninstaller
```

This repository is the reusable source template. It is not itself a target project using memory.

## Design Principles

- Markdown first.
- Project local.
- Agent agnostic.
- Progressive disclosure over broad context loading.
- Rules are prescriptive; memories are descriptive.
- Reports are history, not default context.
- Uncertainties are first-class blockers.
- Native wrappers stay thin and delegate to generic workflows.

## Status

This is a local scaffold and tooling package. It does not publish an npm package and does not require a runtime service.
