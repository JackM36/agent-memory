# 0008 — Knowledge routing, remote writes and collection management

- **Status:** accepted, pending owner testing
- **Date:** 2026-11-04
- **Plan:** follows ADR 0007; changes §5.11 (writes), §7.1, §7.3 and §10

After Phases 0–6, the owner asked how project memory relates to shared knowledge. Three gaps came out of
that discussion:

- Agents could read server collections but not write to them.
- Nothing said where reusable knowledge goes.
- A local "library for all projects" needed hand-edited JSON.

This ADR records the changes that close those gaps.

## Decisions

### Destinations are configured per kind

`writes.defaultCollection` is replaced by two settings: `writes.memory` and `writes.knowledge`. Nothing
was released, so there is no migration.

| Setting | Receives | Allowed in |
|---|---|---|
| `writes.memory` | `memory`, `uncertainty`, `report`, `note` | Project files only (`brain init` sets it) |
| `writes.knowledge` | `knowledge`; falls back to `writes.memory` | User file and project files; the project wins |

Routing follows the entry kind, because the kinds already separate "true about this repository" (`memory`)
from "true anywhere" (`knowledge`). The name `general` was avoided because it is already a scope value.

A `knowledge` target is valid if either:
- it is a local collection with `maintain` or `propose`, or
- a connection serves it.

When the connection lists no collections, the host decides when the client writes.

### The destination's capabilities decide how a save lands

`WriteRouter` is the single client-side entry point for CLI and MCP writes. It works as follows:

1. It finds the target, locally first, then on connections.
2. It takes the caller's effective capabilities there. For a remote target, those are what the host grants
   the key (`GET /v1/collections`, asked once per process).
3. It acts on them:
   - with `maintain`, it writes directly;
   - with `propose` only, it records a proposal and reports that the save became one;
   - otherwise, it refuses.

As a result, agents no longer need to know a collection's review policy, and the instruction "curated
collections: propose" is gone. The owner chooses auto-add or review per collection, through the
registration or the key scopes.

The server does not use the router. Its endpoints call the operations directly, so `POST /v1/entries`
never silently turns into a proposal.

### Remote writes forward intent, not prepared changes

`RemoteHttpProvider` implements `IRemoteWriteTarget`. It sends the caller's request (`ChangeRequestDto`)
to the existing routes. The host then:
- generates IDs;
- stamps dates;
- validates;
- scans for secrets;
- checks revisions.

Preparing the change on the client too would run the pipeline twice and could pick a different ID than
the host. For that reason, remote collections are deliberately not in `CollectionCatalog`.

Two optional fields were added to `ChangeRequestDto`:
- `technologies`, so knowledge can be marked technology-specific in a single shared library;
- `allowSecretRules`, the HTTP form of `--allow-secret-rule`.

Both are additive, so the protocol stays at 1.

Writes run under their own deadline (the connection timeout, at least 30 seconds). A timeout becomes
`provider_unavailable` with a warning that the change may have been applied. It is not an exception.

### Proposals across hosts

`brain proposals list` shows local proposals and those on every connection. Remote IDs are shown as
`connection:id`, and `show`, `accept` and `reject` route on that prefix. Local IDs are unchanged.
`ProposalView` is the shared shape for rendering both.

### Reads from connections that serve "everything"

A connection without a collection list previously had no known collections, so `get` and `relations`
could not route to it. Such sources now act as a fallback after sources that list the collection. A
not-found answer from one of them moves on to the next source.

### Promotion is mechanical; generalising is the agent's job

Agent Brain makes no model calls, so promotion is split in two:

- `brain promote <ref> [--to c]` copies an entry into the knowledge collection as `knowledge`. It keeps the
  text, drops files and symbols, records `promoted from <ref>` as evidence, and optionally supersedes the
  original.
- The MCP prompt `promote` asks the agent to review project memory and save general versions, with project
  names, paths and class names removed. The agent starts this only when the user asks; it is never
  automatic.

### Collection management without JSON editing

| Command | Effect |
|---|---|
| `brain collection create <id>` | Writes a general-scope `collection.json` (default folder `~/agent-brain/<id>`) and registers it in the user file, or in the project with `--project`. `--review` registers `read, propose, publish` instead of `read, maintain`. `--knowledge` sets `writes.knowledge`. |
| `brain collection add <path>` | Registers an existing collection, by default read-only. This is the way to search another project's memory from every project. |
| `brain-server collections add\|list` | Edits `host.json`, which matters because the container has no shell. Markdown collections get a folder and descriptor under `/data`. |

A project that registers its own memory under the same name as a user-file entry keeps its own
registration. A test covers that it stays writable and is indexed once.

### Agent instructions, version 2

The `AGENTS.md` section and the skill now say:
- where each kind goes;
- that a reusable lesson gets two entries (project memory with details, general knowledge without them);
- that a save may come back as a proposal.

The integration version is now 2, so `brain doctor` reports older installs and `brain init` updates them.

## Verification (2026-11-04, Windows x64)

| Measure | Value |
|---|---|
| Tests | 497, all passing (new: router, remote writes against the in-process host, knowledge config, collection commands, promote, host.json editing, open-source routing) |
| Evaluation | Unchanged: 38/43 variants, 0 irrelevant, 0 monotonicity violations |
| AOT `brain.exe` | 21.7 MB, no AOT or trim warnings |
| MCP `tools/list` | 6,056 bytes, about 1,510 tokens (5,769 in ADR 0007); the `save` description carries the routing rule and the new `technologies` parameter |

End to end, with the AOT CLI against a locally running `brain-server`:
- `brain new knowledge --tech unity` was written to the server's `library`, and a memory stayed in the
  project.
- Search and `get` reached the server entry through a connection without a collection list.
- A propose-only key's save became `personal:p-…`, which another key with `publish` listed and accepted
  with `brain proposals accept personal:p-…`.

## Not changed

- **Deletion over MCP is still not offered.** Remote deletes are available from the CLI.
- **Collection aliases remain global** (ADR 0006).
- **Remote collections are never written by the server on a client's behalf.** Federation stays one hop.
