# MCP server

`brain mcp` runs an MCP server over stdin/stdout named `agent-brain`. It uses the configuration of the
directory it starts in, exactly like the CLI. stdout carries only protocol messages; logs go to stderr.

`brain init --integrations …` registers it for each agent. Formats change between product versions;
`brain doctor` checks the registration. Written today:

| Agent | File | Entry |
|---|---|---|
| Claude Code | `.mcp.json` | `mcpServers.agent-brain = { "command": "brain", "args": ["mcp"] }` |
| Codex | `.codex/config.toml` | `[mcp_servers.agent-brain]` `command = "brain"`, `args = ["mcp"]` |
| Gemini CLI | `.gemini/settings.json` | `mcpServers.agent-brain = { "command": "brain", "args": ["mcp"] }` |
| OpenCode | `opencode.json` | `mcp.agent-brain = { "type": "local", "command": ["brain", "mcp"], "enabled": true }` |

## Tools

Seven tools. Their definitions load into every agent session, so descriptions are kept short.

| Tool | Does | Annotations |
|---|---|---|
| `search` | Ranked, budgeted discovery (`query`, `files`, `symbols`, `kinds`, `relaxation`, `includeHistory`) | read-only |
| `get` | Entries or sections by reference (`refs`, `withReferences`) | read-only |
| `list` | Exact listing (`kinds`, `tags`, `blocking`, `cursor`) | read-only |
| `relations` | Outgoing references or backlinks (`entry`, `backlinks`, `cursor`) | read-only |
| `save` | `create`, `update`, `archive`, `supersede`, `resolve`; `knowledge` goes to `writes.knowledge`, other kinds to project memory, `technologies` for technology-specific knowledge; becomes a proposal where the target only takes proposals | write |
| `propose` | An explicit proposal (curated, read-only or remote collections) | write |
| `publish` | Immediate publication where allowed | write, destructive |

All tools return the compact text format described in [cli.md](cli.md). Deletion is CLI-only.

Writes to collections on a connection go to that host, which applies the same validation, secret scan and
revision checks.

## Prompts

| Prompt | Does |
|---|---|
| `promote` | Asks the agent to review project memory for lessons that hold in any project and save general versions as `knowledge` (project names, paths and class names removed). Started by the user, never automatically. |

`publish` is its own tool so agent hosts can require confirmation for it by name. The agent guide also
requires explicit user approval of the exact change before calling it.
