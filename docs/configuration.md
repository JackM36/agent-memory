# Configuration

JSON with comments. Schema: [schema/config.v1.schema.json](schema/config.v1.schema.json). Unknown
properties are errors, so typos surface.

## Files and precedence

Highest first:

1. Per-call options (CLI flags, request fields).
2. `<project>/.agent-brain/config.local.json` — Git-ignored personal overrides.
3. `<project>/.agent-brain/config.json` — committed project configuration.
4. User file — `%APPDATA%\agent-brain\config.json` (Windows), `$XDG_CONFIG_HOME/agent-brain/config.json`
   or `~/.config/agent-brain/config.json` (macOS/Linux). Override the directory with `AGENT_BRAIN_CONFIG_DIR`.
5. Built-in defaults.

The project is `--project <dir>`, else `AGENT_BRAIN_PROJECT`, else the nearest ancestor of the working
directory containing `.agent-brain/config.json`. Without a project, only user-level collections and
connections apply.

`brain config show --origin` prints every effective value with the file that set it.

## Merge rules

| Setting | Rule |
|---|---|
| Single values (limits, relaxation, timeouts) | The highest level that sets one wins |
| `collections`, `connections` | Merged by name; a higher level replaces a same-named entry whole; `"enabled": false` removes it |
| `aliases` | Union by term; the higher level wins a term |
| `project`, `writes.memory`, `runtimeDir`, `proposals`, `inheritUser` | Project files only; ignored with a warning in the user file |
| `writes.knowledge` | Any level; the highest wins, so a project can override the user default |
| `inheritUser: false` | Ignore the user file entirely |

Relative paths resolve against the directory of the file that declares them.

## Example: project

```jsonc
{
  "schema": 1,
  "project": { "id": "space-game", "technologies": ["unity", "csharp"], "packages": { "unity": "2022.3.10f1" } },
  "collections": {
    "space-game.memory": { "path": "memory", "capabilities": ["read", "maintain"] },
    "unity.kb": { "path": "../Packages/com.me.kb/Knowledge~", "capabilities": ["read", "propose"] }
  },
  "writes": { "memory": "space-game.memory", "knowledge": "library" },
  "sourcePreference": ["space-game.memory", "unity.kb", "personal"],
  "search": { "relaxation": "default", "maxResults": 10, "maxTokens": 1000, "kinds": ["knowledge", "memory", "uncertainty"], "log": false }
}
```

## Example: user (a personal server for every project)

```jsonc
{
  "schema": 1,
  "connections": {
    "personal": {
      "url": "https://brain.example.com", "credentialEnv": "AGENT_BRAIN_PERSONAL_KEY",
      "collections": ["general", "unity"], "timeoutMs": 3000,
      "forward": { "project": false, "technologies": true, "files": false, "symbols": false }
    }
  },
  "aliases": { "ecs": ["entity component system"] }
}
```

`brain connection add` writes this for you.

## Example: user (a personal library and other projects' memory)

```jsonc
{
  "schema": 1,
  "collections": {
    "library":         { "path": "C:/Users/me/agent-brain/library", "capabilities": ["read", "maintain"] },
    "library.unity":   { "path": "C:/Users/me/agent-brain/library.unity", "capabilities": ["read", "propose", "publish"] },
    "web-shop.memory": { "path": "C:/code/web-shop/.agent-brain/memory", "capabilities": ["read"] }
  },
  "writes": { "knowledge": "library" }
}
```

`brain collection create library --knowledge` and `brain collection add <path>` write these entries.
Every project now searches the library and the other project's memory; knowledge is saved to `library`
directly, and `library.unity` only takes proposals, which you accept with `brain proposals accept`. A
project that registers its own memory under the same name keeps its own registration, so it stays
writable there.

## Where saves go

| Kind | Default destination | Set by |
|---|---|---|
| `knowledge` | `writes.knowledge`, else `writes.memory` | User file or project (the project wins) |
| `memory`, `uncertainty`, `report`, `note` | `writes.memory` | Project only (`brain init`) |

The destination may be a local collection or one served by a connection. Its capabilities decide how a
save lands: `maintain` writes directly, `propose` alone turns the save into a proposal, neither refuses.
For a connection, those are the capabilities the host grants the key.

## Capabilities

`read`, `maintain` (direct writes), `propose`, `publish`. A collection whose `collection.json` says
`"readOnly": true` (packages) is read-only whatever its registration says. Immediate publishing also
needs `"immediatePublish": true` on the registration; it is off by default.

## Context forwarding

Remote connections receive no project context unless their `forward` settings allow it (all off by
default). A project can switch forwarding off for every connection with `"forwardContext": false`.
File paths outside the project are never sent.

## Search settings

| Setting | Default | Range |
|---|---|---|
| `search.relaxation` | `default` | `strict`, `default`, `broad` or a number in [0, 1] |
| `search.maxResults` | 10 | 1–100 |
| `search.maxTokens` | 1000 | 50–100000 (estimate; converted at 4 bytes per token) |
| `search.maxBytes` | 4000 | 256–10000000 (hard limit for the whole response) |
| `search.candidatesPerSource` | 20 | 1–500 |
| `search.kinds` | knowledge, memory, uncertainty | any kinds |
| `search.log` | false | opt-in local query log in `local/state/query-log.jsonl` |
| `get.maxBytes` / `get.batchTargets` / `get.batchBytes` | 24576 / 10 / 49152 | |

## Decisions (heuristic or model-backed)

Search and writing make a few judgements: does a candidate answer the query, is a new entry a duplicate, what
kind is it, is it worth remembering. Each is a *port* with a built-in heuristic (the default) and an optional
model-backed implementation (Jev, from TypeSafe AI). Nothing contacts a model unless you ask for it. The design
and its rules are in [ADR 0010](decisions/0010-pluggable-decision-providers.md).

```json
{
  "decisions": {
    "jev": { "apiKeyEnv": "TYPESAFE_API_KEY", "model": "jev-1.14.0", "timeoutMs": 1500 },
    "relevance": "hybrid",
    "classifier": "jev",
    "duplicates": "jev",
    "captureGate": "off"
  },
  "collections": {
    "library": { "path": "~/agent-brain/library", "capabilities": ["read", "maintain"], "allowExternalDecisions": true }
  }
}
```

| Setting | Default | Values / range |
|---|---|---|
| `decisions.relevance` | `heuristic` | `heuristic`, `jev` (the model admits and ranks), `hybrid` (both, blended) |
| `decisions.classifier` | `heuristic` | `heuristic` (the agent's kind stands), `jev` (adds a note when it disagrees) |
| `decisions.duplicates` | `heuristic` | `heuristic` (lexical list), `jev` (adds a verdict per similar entry, drops unrelated ones) |
| `decisions.captureGate` | `off` | `off`, `jev` (used by `brain decisions gate`) |
| `decisions.jev.apiKeyEnv` | `TYPESAFE_API_KEY` | the name of an environment variable; never the key |
| `decisions.jev.model` | `jev-1.14.0` | a versioned ID; `jev-latest` and `jev-preview` move and are warned about |
| `decisions.jev.timeoutMs` | 1500 | 100–120000, the whole deadline of one call including retries |
| `decisions.jev.maxConcurrency` | 12 | 1–64 |
| `decisions.jev.maxCallsPerOperation` | 150 | 1–2000; candidates beyond it keep the heuristic verdict |
| `decisions.jev.minConfidence` | 0.6 | 0–1; below it a classification or verdict is not reported |
| `decisions.jev.relevancePool` | 100 | 1–500; with a model-backed relevance port the candidate limit is raised to this |
| `collections.<id>.allowExternalDecisions` | false | whether this collection's titles, summaries, tags and body excerpts may be sent |

- **Privacy.** Text from a collection without `allowExternalDecisions` is only ever judged by heuristics, even when a
  port says `jev`. Bodies are never sent to the relevance judge. Set the flag with
  `brain decisions allow <collection>` (and `deny`); it edits the file that registers the collection.
- **Fallback.** A timeout, `429`/`529`, a missing key or an invalid answer falls back to the heuristic and adds a
  `note:` line to the response. Filtering, budgeting and authorization never depend on the model.
- **Explicit choices win.** The classifier only advises; the kind and destination you give are used.
- **Check it.** `brain decisions status` shows each port's mode, whether the key variable is set, which collections
  may be sent, and what would keep a model from being used. To compare implementations on the evaluation corpus run
  `dotnet run --project eval/AgentBrain.Evaluation -c Release -- --compare heuristic,jev,hybrid`; a model run is skipped
  (and says so) when the key is not set.
