# 0007 — Writes, remote hosting, federation and hardening (Phases 3–6)

- **Status:** accepted, pending owner testing
- **Date:** 2026-11-02
- **Plan:** §9.4–§9.7, §10

Phases 3 to 6 were implemented in one pass at the owner's request ("finish all phases first before we
test anything"). The plan's exit gates between phases were not held. In particular, **the Phase 4
dogfooding period (two real projects for about three weeks, with a go/no-go for Phase 5) was skipped
at the owner's request.** The calibration and scope adjustments it was meant to produce are still
open. They should be revisited once the owner has used the tool on real projects.

## Design decisions made while implementing

### Writes (Phase 3)

- **One write path.** Maintenance, proposals and publishing all go through `ChangePreparer` (validation,
  normalisation, secret scan, revision check) and `ChangeApplier`. Each operation calls
  `CapabilityResolver` once. The authorization sweep test runs every write operation against a
  read-only collection and a propose-only caller, locally and over HTTP.
- **Markdown batches** follow A6: everything is validated first, then the batch writes atomic single files
  sequentially, with a batch marker for detecting a crash afterwards. The kill-point tests inject failures before the temp
  write, after it, before the rename and mid-batch.
- **Creating an entry never blocks on near-duplicates.** The similar-entry check runs on create, and its
  findings are returned with the result (`similar`), so the agent can merge or archive. A hard refusal
  would stop legitimate entries that happen to share words.

### Server (Phase 5)

- **Proposals, the batch journal and the audit log are files under the data directory**
  (`proposals/`, `state/batches/`, `audit/*.jsonl`). The plan placed them in the server database
  (`SqliteProposalStore`, audit tables). The file stores already existed for local use. They keep the
  database to keys and entries, and they are easy to inspect and back up. SQLite batches are still one
  transaction.
- **Action routes use path segments** (`POST /v1/entries/{c}/{e}/archive`) instead of `:archive`
  suffixes. ASP.NET route templates cannot bind a parameter followed by a literal `:` suffix without
  custom constraints. `entries:batchGet` keeps its name because it has no parameter before the colon.
- **One SQLite connection behind a gate.** Requests and the Markdown watcher hold `HostDatabase.Gate`
  for their duration. This serialises the server. It is acceptable at the A12 envelope (tens to
  thousands of entries, a handful of clients) and keeps SQLite single-threaded. A connection pool
  is the upgrade path if concurrency is ever needed.
- **Every request refreshes the index incrementally** (a stat scan of the hosted Markdown roots, about 40 ms at 10k
  entries). The watcher therefore only shortens the time until changes are picked up. Reconciliation
  is tested with notifications turned off (`WatcherTests`).
- **`allowInsecureHttp` is a `host.json` setting**, not a command-line flag, so it is visible in the same
  file as the transport it weakens.
- **`local` transport** binds loopback only and still uses ordinary API keys. The plan's "generated
  local key in `.agent-brain/local/state/`" was not implemented: `brain-server init` already prints a
  key, and the client reads it from an environment variable like any other connection.
- **Collection aliases remain global** (ADR 0006), including for remote sources. Aliases expand only on
  the client.
- **Search results carry structured applicability** (`applicability` in the JSON summary), so a remote
  source's results can be ranked and conflict-checked on the client exactly like local ones.
- **The provider contract suite and federated list cursors** landed in Phase 5, as ADR 0006 planned.

### Federation (Phase 5)

- Sources are merged by rank. Duplicate identities collapse to their best rank. When copies of an entry
  disagree on revision, the result carries `conflict` with every revision and source.
  `sourcePreference` decides which copy is shown.
- A request problem, such as an over-long query, is validated once before fan-out. Previously each
  source rejected the query on its own, and the merged error read "no source could be searched".
  Source failures now include each source's reason.

### Deployment (A13)

- **Image:** multi-stage. The build stage runs natively and cross-targets with `-a $TARGETARCH`, so the
  arm64 image is not compiled under emulation. The runtime is the chiseled ASP.NET image (non-root, no
  shell). `/data` is pre-created and owned by the app user, so a named volume is writable.
  `HEALTHCHECK` runs `brain-server health`.
- **Compose:** `brain-server` has no published ports; Caddy terminates TLS and proxies to it. The
  default `trustedProxies` (the private ranges) cover the Compose network.
- **Request body limit:** 1 MiB in Kestrel.

## Phase 6 hardening

| Check | Where |
|---|---|
| FTS injection fuzzing: 10,000 random queries built from FTS operators, quotes, NUL, bidi and emoji never error or alter the index | `Infrastructure.Sqlite.Tests/FtsFuzzTests` |
| Path traversal in entry identities, sections and proposal IDs (encoded `../`, `\`, `%00`, upper case) | `Server.Tests/SecurityTests`, plus Domain slug tests |
| Oversized input: query length (400), body size (Kestrel limit; TestServer does not enforce it, so the test accepts 400 or 413) | `SecurityTests` |
| Key secrets never appear in responses, valid or rejected | `SecurityTests.Keys_never_appear_in_responses` |
| `entry://` links never cause network access: links resolve only through the collection catalog; there is no URL fetch path | by construction (`EntryLink` parses only `entry://<collection>/<id>`) |

A test-harness race was found and fixed: tests created keys outside the database gate while the watcher's
start-up sync was running, which corrupted the shared connection's command list.

## Measurements (2026-11-02, Windows x64)

| Measure | Value |
|---|---|
| Tests | 478, all passing |
| Evaluation (Porter, phase 5 cases) | 38/43 variants (88.4%), 0 irrelevant, 0 monotonicity violations; mean response 442 bytes, p95 latency 6.3 ms |
| AOT `brain.exe` | 20.4 MB (17.2 MB in Phase 2; the HTTP client and write paths were added). No AOT or trim warnings |
| MCP `tools/list`, 7 tools | 5,769 bytes, about 1,440 tokens (6,740 before `ToolSchemaCompactor` removed `["T","null"]` and `default: null` noise) |

The tool definitions exceed the plan's ~1,200-token target by about 20%. The remainder is descriptions
that carry agent instructions, plus the read-only and destructive annotations that clients use for
permissions. These were kept deliberately.

## Not verified

- **The Docker image was not built here.** No Docker daemon was available in this environment. The
  cross-targeted `dotnet publish` the image runs was verified (`linux-x64`, framework-dependent).
  The Compose stack, the Caddy certificate flow and the "five commands on a fresh VM" acceptance
  (plan §9.6) need the owner's Droplet.
- **The release workflow has not run.** The win-arm64, osx-x64 and linux-arm64 builds are untested.
- **The exit gates of Phases 2–5 are open**: owner acceptance of the baselines, an end-to-end `brain init`
  plus legacy import on a real project, dogfooding, and a live Droplet used from two projects.
