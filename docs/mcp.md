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
| `save` | Project memory: `create`, `update`, `archive`, `supersede`, `resolve` | write |
| `propose` | Proposal to curated or read-only knowledge | write |
| `publish` | Immediate publication where allowed | write, destructive |

All tools return the compact text format described in [cli.md](cli.md). Deletion is CLI-only.

`publish` is its own tool so agent hosts can require confirmation for it by name. The agent guide also
requires explicit user approval of the exact change before calling it.
