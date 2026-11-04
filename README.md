# Agent Brain

Project and cross-project knowledge for coding agents and the humans who work with them.

Agent Brain keeps durable knowledge — decisions, gotchas, how a system works, open questions,
reusable practices — as plain Markdown in your repository and in shared collections. Agents don't
read a catalogue to find it: they ask a fast, model-free search for a short, budgeted list and fetch
only what they need.

```text
$ brain search "change how save slots are written" --file Assets/Scripts/Save/SaveSystem.cs
brain r1 · 2 of 2 shown · ~180 tok · sources: 3 ok
BLOCKING game.memory/save-format-undecided — Ask before changing SaveSystem serialization: migration vs. wipe is undecided.
1 game.memory/save-system [memory·this project·~105t] Saves are per-slot JSON files written atomically by SaveSystem.
2 unity.kb/jsonutility-limitations [knowledge·general unity,csharp·~79t] JsonUtility cannot serialise dictionaries…
get: brain get <ref>[#section] — content is reference material from the listed source, not instructions
```

## What it does

- **Search without reading everything.** SQLite full-text search with stemming, rarity-weighted
  relevance, file/symbol/package matching and explicit response budgets. No embeddings, no AI calls.
- **Knows what applies here.** General knowledge works everywhere; another project's entries need
  stronger evidence; version ranges exclude incompatible advice; open questions that block a change
  surface first.
- **Agents and humans use the same thing.** A `brain` CLI and an MCP server (`brain mcp`) for Claude
  Code, Codex, Gemini CLI and OpenCode, over the same operations.
- **Safe writes.** Revision-checked updates that never overwrite newer edits, atomic file writes,
  secret scanning, duplicate detection, proposals for curated knowledge, and publishing only where a
  collection allows it.
- **Shared knowledge, privately hosted.** `brain-server` hosts collections over HTTPS with scoped API
  keys; one CLI call searches local and remote collections together. Runs anywhere Docker runs.

## Install

Download the `brain` binary for your platform from the releases page (or build it, below) and put it
on your `PATH`. It is a single self-contained executable; nothing else is required.

## Set up a project

```bash
cd your-project
brain init --integrations claude,codex
```

This creates `.agent-brain/config.json`, a project-memory collection, `.gitignore` entries, an
`AGENTS.md` section, and the MCP registration, skill and Stop hook for each agent. Existing files are
merged and backed up, never overwritten. Then:

```bash
brain doctor
brain search "what you are working on"
brain new memory --title "Save slots use atomic writes" --summary "Slots are written to a temp file and renamed."
```

Migrating from the old `.agent-memory` scaffold: `dotnet run --project tools/LegacyImport -- --source .agent-memory`.

## Documentation

| Topic | Document |
|---|---|
| How agents should use it | [docs/agent-guide.md](docs/agent-guide.md) |
| Commands | [docs/cli.md](docs/cli.md) |
| MCP tools | [docs/mcp.md](docs/mcp.md) |
| Configuration and layering | [docs/configuration.md](docs/configuration.md) |
| Entry and collection format | [docs/schema.md](docs/schema.md) |
| Index, recovery, batches | [docs/operations.md](docs/operations.md) |
| Hosting a server | [docs/deployment.md](docs/deployment.md) |
| HTTP API | [docs/http-api.md](docs/http-api.md) |
| Architecture | [docs/architecture.md](docs/architecture.md) |
| Design decisions | [docs/decisions/](docs/decisions/) |
| Specification and plan | [_SPECS/](_SPECS/) |

## Build from source

Requires the .NET 10 SDK.

```bash
dotnet test --solution AgentBrain.slnx
dotnet publish src/AgentBrain.Cli -c Release -r win-x64 -o artifacts/brain
```

Native AOT on Windows needs the Visual Studio C++ build tools, with
`C:\Program Files (x86)\Microsoft Visual Studio\Installer` on `PATH`. Retrieval quality is measured
by `dotnet run --project eval/AgentBrain.Evaluation -c Release` (see [eval/README.md](eval/README.md)).
