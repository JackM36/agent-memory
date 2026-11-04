# Architecture

```text
Human / agent (CLI, MCP) / future Unity wrapper / future web UI
                    │
          brain CLI · brain mcp · brain-server (HTTP)
                    │
               Composition  ── wires everything identically for every host
                    │
              Application operations
   search · list · get · relations · maintenance · proposals · publishing · index
                    │
   RetrievalCoordinator ── LocalKnowledgeProvider (shared SQLite FTS5 index)
                       └── RemoteHttpProvider (one hop to brain-server)
                    │
   Storage: Markdown collections · SQLite collections (servers)
```

## Projects

| Project | Responsibility |
|---|---|
| `AgentBrain.Domain` | Identifiers, metadata, applicability, version ranges, globs, anchors, links, revisions. No I/O. |
| `AgentBrain.Application` | Ports, operations, retrieval pipeline, configuration layering, capture rules, authorization. |
| `AgentBrain.Contracts` | Versioned wire DTOs and renderers (compact, human, JSON). |
| `AgentBrain.Infrastructure.Markdown` | Frontmatter, Markdown analysis, collection scanning, atomic writes. |
| `AgentBrain.Infrastructure.Sqlite` | FTS5 search index; SQLite entry and key storage for servers. |
| `AgentBrain.Infrastructure.Local` | Configuration files, locks, proposals, audit log, batch markers, query log. |
| `AgentBrain.Infrastructure.Http` | Remote provider client. |
| `AgentBrain.Composition` | The one composition root. |
| `AgentBrain.Mcp`, `AgentBrain.Integrations`, `AgentBrain.Cli` | MCP tools; init/doctor/hooks; the `brain` command. |
| `AgentBrain.Server` | `brain-server`: HTTP API, API keys, file watching. |

Dependency rules are enforced by `AgentBrain.Architecture.Tests`: Domain depends on nothing; Application
only on Domain; infrastructure never on each other; only Composition sees all infrastructure.

## Retrieval

1. **Analyse** the query into concepts: stopwords dropped, identifiers split (`SaveSystem` → `save system`),
   aliases added as equivalent terms of the same concept. Raw text never reaches SQL.
2. **Filter** in SQL: collections, kinds, tags, publication, retirement, resolved questions and version
   incompatibility — before any ranking.
3. **Candidates**: BM25 top-N plus structural matches (files, symbols, packages) and the project's blocking
   questions.
4. **Admit** on query-relative evidence: rarity-weighted concept coverage plus structural matches, against a
   threshold per applicability class (same project < general < other project), lowered by relaxation.
5. **Rank** by coverage, lexical rank, structure, applicability and provenance; ties break on identity.
6. **Blocking** questions that match go into their own section.
7. **Budget**: the complete rendered response is measured in bytes; blocking first, then results in rank
   order until the next one would not fit.

Remote sources apply the same steps on their side; the coordinator only merges by rank, collapses
duplicate identities and reports differing revisions.

Constants and their calibration: [decisions/0006-local-retrieval.md](decisions/0006-local-retrieval.md).
