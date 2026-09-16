# Agent Knowledge and Memory System — Implementation Specification

## 1. Status and purpose

This document records the design agreed with the project owner before implementation. It is the implementation guide for replacing this repository's manifest-based agent-memory scaffold with a standalone C# knowledge and memory tool.

The owner approved the design and requested this specification before implementation. Creating this document does not authorize incidental scope expansion. Implement the agreed product, using the recommendations below as implementation guidance where exact names or algorithms were not explicitly settled.

**Decision terminology:**

- **Requirement / agreed decision:** settled during the design interview.
- **Suggested implementation:** a concrete way to satisfy a requirement; exact naming and internal layout can change without changing its behavior.
- **Calibration item:** requires experiments or evaluation rather than an invented certainty, such as ranking weights and relevance thresholds.

No migration from the old content format is required. The new tool is the primary direction of this repository. Unity wrapper implementation is explicitly outside this repository's scope.

## 2. Problem and goals

The current system stores Markdown rules, memories, uncertainties, reports, and pending notes. Node scripts scaffold files, regenerate flat manifests, and validate basic metadata. Agents perform relevance selection and curation.

Its central scaling problem is that agents must inspect a catalogue of active entries before knowing which content to retrieve. Selective body retrieval does not remove the growing context cost of reading the catalogue. Splitting manifests is suggested by the old protocol but indexed, bounded retrieval is not implemented.

The replacement must move discovery out of the agent's context and into a deterministic retrieval application:

1. A short instruction tells the agent how to query the tool.
2. The tool searches registered local and remote collections.
3. The caller receives a lean, bounded list of relevant entries.
4. The caller explicitly fetches only the entries or sections it needs.

The same tool must be usable manually. Agents are clients, not a runtime dependency.

### 2.1 Primary goals

- Personal-first, cross-project knowledge and memory, with authenticated remote hosting.
- Curated reusable knowledge for general software development, Unity, web development, packages, and individual projects.
- Local Markdown content editable in a project and distributable with packages.
- Database-backed authoritative content where desirable, particularly remotely.
- Smart, explainable, model-free retrieval using lexical relevance and structured applicability.
- Cross-project discovery without flooding results with weakly related project material.
- Strong context and token efficiency through progressive disclosure and explicit budgets.
- Human and agent access through equivalent CLI and HTTP application operations.
- Thin host integrations rather than coupling the core to Unity or a particular agent product.
- Clean architecture with cohesive responsibilities and replaceable infrastructure.

### 2.2 Initial operating envelope

Design and evaluate for tens of thousands of entries and approximately 2–10 configured collections. This is a target envelope, not a measured performance claim.

The initial normal-discovery budget target is about 1,000 estimated tokens and at most 10 results. Limits must be configurable. Actual built-in defaults, candidate counts, ranking weights, and thresholds must be validated against the evaluation corpus.

### 2.3 Explicit non-goals for the first release

- Rules, policy distribution, always-read content, or a bootstrap operation for mandatory guidance. The owner will keep rules local to projects outside this system.
- Unity wrapper or Unity package implementation.
- Web management UI. The API must support a future UI.
- Required embeddings, LLM calls, agents, or external AI services.
- Multi-tenant account/organization administration.
- PostgreSQL or multiple server-database implementations immediately.
- Remote-content offline caching or synchronization.
- Recursive federation or server-to-server forwarding chains.
- Recursive entry-reference expansion.
- Semantic duplicate detection, graph-popularity ranking, or automatic text merging.
- Revision-pinned reference retrieval or a guarantee of addressable historical revisions.
- Legacy content migration and indefinite support for the old manifest architecture.

## 3. Agreed system architecture

```text
Human / agent / future Unity wrapper / future web UI
                         |
                   CLI or HTTP API
                         |
                  Application operations
                  |                  |
           Retrieval coordinator    Maintenance / proposals
                  |
         +--------+--------------------+
         |                             |
   Local collection providers    Remote HTTP providers
         |
   +-----+----------------+
   |                      |
Markdown storage      SQLite storage
   |                      |
Rebuildable SQLite    SQLite search indexing
search index
```

### 3.1 Platform and delivery

- Target .NET 10 LTS.
- Use ASP.NET Core for the HTTP host.
- Use SQLite FTS5 for the initial lexical search implementation.
- Support Windows, macOS, and Linux.
- Produce self-contained distributions so consumers need not install the .NET SDK.
- The standalone executable is the integration boundary for Unity; do not target Unity's embedded runtime for the core.
- Support explicit project/configuration paths for automation, plus convenient current-project discovery.

### 3.2 CLI versus local hosting

The CLI executes application logic directly and can query local and remote collections in one invocation. It does not require a running local HTTP bridge.

Local directory providers are still required: CLI is an interface, while directories are content sources. A package contributes a registered knowledge directory, not a server process.

The HTTP host exposes the same application operations. It may run remotely or locally when a client wants HTTP access. A local HTTP host binds to loopback by default and uses a local credential; direct CLI use does not need HTTP authentication.

### 3.3 Boundaries and dependencies

Suggested solution components:

| Component | Responsibilities |
|---|---|
| Domain | Identity, metadata, applicability, lifecycle, revisions, references, invariants |
| Application | Search/list/get, orchestration, maintenance, proposals, capabilities, concurrency |
| Infrastructure.Markdown | Parsing, storage, external edit detection, atomic writes, recovery journal |
| Infrastructure.Sqlite | Authoritative SQLite storage, schema evolution, FTS indexing |
| Infrastructure.Http | Remote provider client, credentials, transport error handling |
| CLI | Argument parsing, project discovery, human and structured output |
| HTTP host | Versioned endpoints, authentication, authorization, transport mapping |
| Tests / evaluation | Contract tests, retrieval corpus, integration and failure scenarios |

Exact assembly splitting is an implementation choice; avoid unnecessary projects for their own sake. Domain and application behavior must not depend on CLI, ASP.NET, Unity, or storage details. Do not introduce one oversized repository interface containing every operation.

Separate abstractions for:

1. **Authoritative entry storage:** content and metadata reads/writes, revisions, listing, lifecycle changes.
2. **Search indexing/retrieval:** indexing lifecycle, filtering, candidate retrieval, ranking evidence.
3. **Retrieval providers:** application-level search/get/list against local collections or remote services.
4. Supporting concerns where needed: credentials, clocks, configuration, journaling, proposal storage.

Reuse application operations between CLI and HTTP. Keep request validation, permission checks, revision handling, and publishing semantics out of transport-specific duplication.

## 4. Configuration and collection discovery

### 4.1 Project configuration only

There is no user-level connection configuration or inheritance layer in the first release.

Project configuration defines:

- Stable current-project identifier and stable contextual information.
- Registered local Markdown and SQLite collections.
- Named remote connections and their hosted collection selections.
- Credential references, not committed credential values.
- Source preference for duplicate identities with disagreeing revisions.
- Which connections participate in queries.
- Query-context forwarding policy per connection.
- Relaxation, result limits, response-size limits, candidate limits, and timeouts.
- Default searched entry types and optional default exact filters.
- Local collection capabilities and default write destinations.
- Runtime-data directory and indexing settings.
- Alias additions.

Local roots must be explicitly registered. Relative paths resolve against the configuration file that declares them, not against an accidental process working directory. Project/package wrappers can supply collection roots through configuration or invocation parameters.

Remote hosts also require their own host configuration for hosted collections, storage roots, credentials, permissions, limits, and publishing enablement. This is server deployment configuration, not user-level project inheritance.

### 4.2 Default precedence

For caller-configurable query settings:

**Per-call override → project configuration → built-in default.**

Host-enforced maximums and authorization remain authoritative. A request cannot use a larger limit to bypass a server maximum.

Submit-and-publish enablement is controlled by the **target host/collection only**, together with caller permissions. Do not add another enablement switch in the calling project's configuration.

### 4.3 Runtime and authored data

Store local indexes, journals, and operational state in one configurable Git-ignored directory under the project. Do not write generated indexes into package content directories.

Store authoritative Markdown and collection identity metadata in registered content roots. Runtime data is not itself a knowledge collection. Durable local proposals must have an explicit storage destination and must not be accidentally discarded as a rebuildable cache.

Remote hosts use a separately configured data directory. Clearly distinguish rebuildable indexes from authoritative database content and durable proposal/journal records.

### 4.4 Collection capabilities

Declare and enforce capabilities such as:

- Read/search/list.
- Direct maintenance.
- Proposal submission.
- Publishing/review.

Read-only package collections stay read-only regardless of a conversational approval. A connection or client cannot elevate the host's capabilities.

Expose capability discovery so clients can offer valid operations without guessing. Use an explicit default destination for project-memory writes; do not infer it from result ranking.

## 5. Content model

### 5.1 Identity

The logical identity of an entry is **collection ID + entry ID**. Both are stable and independent of file path or server address.

- File moves do not change identity.
- Copies of a package collection retaining the IDs represent the same logical content.
- Intentional forks receive new identity and may record origin.
- Get operations use logical identity, with source selection when necessary.
- Return an opaque revision for concurrency checks and copy comparison.

The exact identifier encoding is not settled. Choose URI-safe stable identifiers, define validation and normalization, and consistently use them across adapters.

### 5.2 Entry kinds

| Kind | Meaning | Default retrieval treatment |
|---|---|---|
| knowledge | Reusable explanation, practice, reference material | Published content normally eligible |
| memory | Established observation, decision, project experience | Active published content normally eligible |
| note | Informal working material | Explicit inclusion / appropriate search profile |
| uncertainty | Unresolved question or potentially unsafe assumption | Unresolved published content normally eligible |
| report | Historical investigation or task record | Explicit inclusion / history-oriented search |

Do not implement a rule kind. An entry may describe a historical rule or decision from another project, but retrieved content is reference material, not an instruction that governs the current project.

### 5.3 Independent metadata dimensions

Use facets, not a single general → technology → project hierarchy.

| Concern | Required capabilities |
|---|---|
| Content | Title, concise author-provided summary, Markdown body |
| Identity | Collection ID, entry ID, opaque revision |
| Classification | Kind and topical tags |
| Applicability | Explicit general/project applicability; multiple projects and technologies where needed |
| Constraints | Package and version applicability; optional platform information where relevant |
| Navigation | Optional file associations and symbols |
| Provenance | Origin, curated versus observed source, evidence/verification information |
| Lifecycle | Publication state, archive/supersession state, type-specific state |
| Relationships | Supersedes relationship; extracted entry references |
| Timestamps | Creation/update and verification times where relevant |

Specify exact required/optional fields in the implementation schema before writing adapters. Do not confuse confidence, authority, applicability, and relevance.

General clean-code knowledge remains eligible in Unity and web contexts; eligibility does not mean it must be returned for every query.

Do not infer general applicability from missing project metadata. Use explicit collection defaults and validation. A project-specific entry should not become globally applicable merely because an author omitted a field.

### 5.4 Lifecycle

Keep publication state separate from kind-specific state. For example, an uncertainty can be published and unresolved, then published and resolved. A knowledge entry can be draft or published.

- Default retrieval excludes archived and superseded entries.
- Exact listing can explicitly include them.
- Archive is the normal retirement action.
- Explicit deletion is available as a management operation.
- A supersedes relationship identifies replacement content.
- Age alone must not cause automatic deletion or demotion.
- Resolved uncertainties are excluded from normal unresolved-uncertainty search unless requested.

Avoid an unwieldy universal status enum mixing unrelated dimensions.

## 6. Entry references

### 6.1 Canonical syntax

Use standard Markdown links with a dedicated URI scheme:

```markdown
This follows [Unity serialization guidance](entry://collection-id/entry-id).

See [managed references](entry://collection-id/entry-id#managed-references).
```

The URI contains identity and optional section anchor, not a host address, secret, or filesystem path. Ordinary Markdown viewers can display the label; opening the custom URI requires a supporting client.

Provide a CLI/API helper returning canonical links. Define one deterministic heading-anchor convention shared by link generation, validation, and section retrieval, including duplicate headings and escaping.

### 6.2 Indexing and retrieval behavior

- Extract links into a reference index during indexing; do not require duplicate frontmatter reference lists.
- Retain links in returned Markdown.
- Return outgoing-reference metadata on request.
- Provide paginated backlinks.
- Support bounded batch get for selected referenced entries/sections.
- Do not recursively fetch linked content.
- Do not increase search relevance merely because an entry has many backlinks.
- Resolve links only through configured/authorized sources; never automatically connect to a server named or implied by content.

### 6.3 Validation and revisions

Validate reference syntax strictly. Unresolved targets are diagnostics, not automatic publication blockers, because separately installed packages/collections may supply them later.

Distinguish missing target, unconfigured collection, unavailable source, and missing section where determinable. Respect authorization when reporting details; an inaccessible target need not be disclosed as existing.

References resolve current published content by default. Explicit get reports archived/superseded state and replacement information, without silently substituting a replacement.

Do not promise historical retrieval from concurrency revisions or publishing audit records. Revision-pinned references are deferred until retained, addressable revision history exists.

## 7. Retrieval contract

### 7.1 Search inputs

Support a simple text query for manual use and optional structured context:

- Short task/query description.
- Current project identifier.
- Technologies, packages, and versions.
- Relevant file paths and symbols.
- Tags used as relevance hints.
- Exact filters for tags, kinds, collections, lifecycle, and other supported metadata.
- Relaxation, result limits, response budgets, and diagnostic mode.
- Explicit historical/incompatible-content option.

Stable context may come from project configuration; task-specific context comes from the request. Keep relevance hints distinct from exact filters. A tag hint boosts matching; a tag filter restricts the candidate set.

### 7.2 Eligibility and ranking are different

Apply access control, explicit filters, lifecycle requirements, and known incompatibility rules independently of relevance scoring.

Then rank eligible candidates using:

1. Lexical relevance across title, summary, tags, and body, giving stronger weight to concise descriptive fields.
2. File/symbol, technology, and package matches.
3. Project applicability, including a strong other-project penalty.
4. Modest quality adjustments for curated provenance and verification.

Freshness and self-declared confidence must not make a weak topic match qualify. Durable knowledge is not inherently worse because it is old.

### 7.3 Cross-project and technology applicability

- Same-project context provides useful positive evidence.
- Other-project content remains eligible with a strong penalty and stricter admission requirements.
- Strong task relevance can overcome that penalty.
- A few shared tags alone should usually not qualify other-project entries.
- Technology mismatch incurs a strong penalty unless the query explicitly targets that technology.
- Known version incompatibility excludes content from ordinary search; an explicit option can include it.
- Missing version information means unknown applicability, not verified compatibility.

Origin and applicability travel with results. Another project's decision is reference material outside its original scope, even if highly relevant. The system does not distribute governing rules.

### 7.4 Relaxation

Expose `relaxation` in the normalized range `[0, 1]`:

- `0`: strict relevance admission.
- `1`: broadest supported candidate admission.
- Omitted: resolve through project configuration and built-in defaults.

Relaxation lowers admission requirements, including the stronger other-project threshold, while preserving the ranking function. It is not a relevance probability.

It never overrides authorization, explicit filters, version incompatibility exclusions, or response budgets. The system may return zero or fewer results than requested. Do not fill slots with irrelevant content.

Calibration must establish the built-in relaxation default and monotonic admission behavior on a fixed candidate set. Bounded candidate generation may affect recall; expose that in diagnostics instead of claiming exhaustive ranked search.

### 7.5 Lexical baseline and aliases

Use SQLite FTS5 as the model-free baseline. Normalize tags and structured identifiers. Support explicit aliases, such as a carefully defined equivalence between an abbreviation and its expanded form.

- Ship only a small set of unambiguous aliases.
- Allow project and collection additions.
- Keep package terminology with its collection when appropriate.
- Preserve original query terms alongside expansions.
- Avoid broad, automatic synonym expansion that makes unrelated material eligible.

Version the retrieval behavior and request contract. Keep internal weights centrally defined initially rather than exposing every scoring coefficient. Add advanced overrides only when evaluation demonstrates a need.

### 7.6 Discovery result shape

Normal results should contain only what the caller needs to decide whether to fetch:

- Logical identity.
- Title and very short author-provided summary.
- Tags and kind.
- Origin and compact applicability information.
- Path/source locator where meaningful.
- Estimated content size.
- Revision/source-conflict information when relevant.

Do not return complete bodies, lengthy evidence, full diagnostic traces, or all outgoing references by default. Provide those through get or explicit diagnostic options.

Exact field names are implementation choices. Keep schemas consistent and documented across CLI JSON and HTTP.

### 7.7 Search versus list versus get

**Search:** ranked and budgeted; supports relaxation and relevance admission.

**List:** exact filters, explicit `all`/`any` tag matching, stable ordering, pagination. Must allow exhaustive traversal without a relevance cutoff. Handle federation pagination deliberately; do not disguise a truncated search as complete listing.

**Get:** identified full content or a heading-based section; exposes lifecycle/revision metadata. Batch get is bounded and reports per-target failures.

**References/backlinks:** explicit, paginated relationship inspection.

Content truncation must be explicit and explain how to retrieve the remaining content. Search summaries are authored, not generated by an AI dependency.

### 7.8 Budgets

Support:

- Hard result-count limit.
- Hard serialized response-size limit, defined consistently in bytes.
- Configurable estimated-token budget, labelled as an estimate.
- Bounded internal candidate counts and content retrieval sizes.

Budget the complete serialized response, including metadata, JSON overhead, and source failure notices. Omit lower-ranked results rather than silently cutting summary text. If a requested budget cannot fit even a minimal valid envelope, return a clear validation/budget error rather than violating the limit.

No model-independent exact token guarantee is possible. An exact tokenizer integration is not required for the first release.

## 8. Federation

### 8.1 One-hop topology

The project-side coordinator queries local providers and configured remote servers. Remote search endpoints search hosted collections only; they do not forward to additional servers.

Share hosted-collection search logic with local providers without accidentally invoking coordinator recursion.

### 8.2 Query execution and failures

- Query eligible sources concurrently.
- Use per-source timeouts and propagate cancellation.
- Forward only the context permitted for each connection.
- Do not forward project-specific context or file paths unless enabled.
- Return partial results with compact unavailable-source information.
- Distinguish an empty successful search from an incomplete search caused by failures.
- Start with live queries; do not add offline remote caches implicitly.

### 8.3 Merging and relevance

Raw scores from different indexes are not comparable. Use rank-based fusion as the initial baseline, with applicability information retained and used in final ranking.

Each source applies relevance admission before returning candidates; being first in an irrelevant source must not make a result qualify. Specify the fusion formula, source grouping, tie-breaking, and how applicability is used without accidentally applying penalties twice. These are calibration/implementation details, not already established numeric constants.

Fetch more candidates internally than the final response contains, within configurable bounds. An illustrative starting point is 20 candidates per source for a final limit of 10; this is not a mandatory universal ratio.

Evaluate source-size differences, weak-source first results, and duplicate source exposure. Do not assume fusion alone yields perfect global relevance.

### 8.4 Duplicates and revision disagreements

- Collapse identical collection ID + entry ID into one logical result.
- Retain source provenance.
- Keep independently authored entries separate, even if wording/topics overlap.
- Report differing revisions for the same identity.
- Select the default fetch source using explicit source preference.
- Do not claim the preferred copy is newest unless that is actually established.

Define deterministic behavior when preference is absent or the preferred source is unavailable, and surface the selected source. Do not let duplicate replicas silently inflate relevance without a deliberate, evaluated policy.

## 9. Storage and indexing

### 9.1 Markdown adapter

Markdown plus structured frontmatter is authoritative. Use a robust parser and explicit validation rather than carrying forward the legacy handwritten YAML subset accidentally.

Support direct human edits, package distribution, and read-only roots. Include stable collection metadata and entry identity. Preserve bodies and avoid unnecessary reformatting during metadata updates.

Implement clear diagnostics for malformed files, duplicate IDs, unsupported schema versions, invalid applicability, and malformed references. One invalid file should be identifiable without silently corrupting the index.

### 9.2 SQLite adapter

Provide authoritative entry/proposal storage and full-text indexing. Database schema changes require explicit versioning and upgrade handling; this is internal product schema evolution, not legacy content migration.

Use transactions for content, lifecycle, proposal, and index changes where they share the database. Avoid implying a transaction can span separate remote collections.

### 9.3 Index freshness

- CLI/API writes update affected indexes as part of their application operation.
- Before CLI search, incrementally inspect registered Markdown sources and reindex changed entries.
- Track file metadata and content hashes; do not parse every unchanged body on every query.
- Provide explicit full verification/reindex to detect changes not captured by cheap metadata checks.
- Long-running hosts use file notifications plus periodic reconciliation because watchers can miss events.
- Detect additions, edits, moves, deletions, applicability changes, and reference changes.
- Treat package collections as read-only unless configured otherwise.

Separate authoritative write success from index freshness in error handling. A failed indexing step must not cause an unreported divergence. Journaling/recovery or explicit stale-index diagnostics must make recovery possible.

## 10. Maintenance, proposals, and permissions

### 10.1 Two ordinary write workflows

**Designated project-memory collections:** agents may directly maintain entries under configured capabilities. Automatic maintenance remains allowed; the owner's explicit conversational approval requirement does not apply universally to all memory writes.

**Curated knowledge:** agents submit proposals/drafts, or use submit-and-publish after receiving explicit approval. Published content remains unchanged while an ordinary proposal awaits acceptance.

Capabilities belong to collections/callers, not merely to entry type labels. Prevent a caller from bypassing curated publishing requirements through a different generic update endpoint.

### 10.2 Proposal operations

Support through CLI and HTTP:

- Propose a new entry or edits to an existing one.
- Record target identity, base revision, proposed content/operations, and attribution.
- List and inspect pending proposals.
- Accept or reject with publishing permissions.
- Record review outcome.

If a source is read-only or cannot accept proposals, allow an explicit local proposal referencing the source identity and revision. Do not silently edit the package or create a replacement pretending to be the original.

### 10.3 Submit-and-publish

Provide an additional application operation exposed through CLI and HTTP that submits a proposal and publishes it immediately, preserving an audit record and all revision checks.

Requirements:

- Disabled by default on the target host/collection.
- Requires target enablement and caller publishing permission.
- No additional enablement requirement in the calling project's configuration.
- Does not bypass read-only status, validation, concurrency, or version rules.
- Ordinary proposal/review remains available when disabled.
- Do not describe Markdown multi-file publication as fully atomic; use the recoverable mechanism below.

Agent guidance must require:

1. Show the exact proposed curated changes or an unambiguous diff/batch.
2. Ask the user for permission.
3. Wait for explicit approval. Silence, an unrelated answer, or lack of response is not approval.
4. Execute only the approved changes.
5. Ask again if content changes after approval, including changes made to resolve a revision conflict.

The API cannot verify conversational approval. Enforcement consists of agent instructions plus server capability/permission checks; no separate human approval-token mechanism was agreed.

### 10.4 Concurrency

- Reads return opaque revisions.
- Updates provide the revision they were based on.
- Reject stale updates and provide sufficient conflict details for review/retry.
- Detect external Markdown edits before replacing files.
- Use atomic single-file replacement where supported.
- Do not silently overwrite newer content or automatically merge conflicting text.

### 10.5 Batches and recovery

One explicit approval may cover a clearly specified batch. A batch targets one collection only.

- Validate all revisions and permissions before publication.
- Reject the batch if any target has changed.
- Use SQLite transactions for database-backed batches.
- Use a recoverable write journal for Markdown multi-file batches.
- Define journal states, startup recovery, interruption behavior, and incomplete-recovery reporting.
- Prevent concurrent writers from defeating revision checks between validation and replacement; select and test an appropriate locking/coordination strategy.

Document the limits of ordinary filesystems under external concurrent editing. Do not promise cross-file atomic visibility or cross-server transactions.

### 10.6 Audit and history

Retain sufficient attribution and outcome information to inspect proposals and publishing actions. An audit record is not a promise of historical snapshot retrieval. Exact retention policy and record layout are implementation details to document.

## 11. Authentication and authorization

Use revocable, scoped API keys sent as bearer tokens over HTTPS for remote access.

- Store server-side key hashes, not recoverable plaintext keys.
- Support revocation and rotation.
- Scope credentials by collection and capability: reading, draft submission, maintenance, publishing.
- Reference credentials from project connection configuration, for example through environment-variable names.
- Do not commit keys or return them in diagnostics/logging.
- Configure local HTTP listeners for loopback by default with local credentials.
- Direct CLI access uses local configured capabilities and filesystem access, not HTTP keys.

No organization/account UI is required. Keep authorization in application behavior so transport choices cannot bypass it.

## 12. Public operation catalogue

The exact route and command spellings are suggested implementation details. Establish a versioned HTTP API, consistent DTOs, and a discoverable CLI. Suggested operation families:

| Family | Operations |
|---|---|
| Discovery | Search, exact list, collection/capability inspection |
| Content | Get, bounded batch get, section retrieval, canonical-link generation |
| Relationships | Outgoing references, paginated backlinks, reference diagnostics |
| Direct maintenance | Create/update, resolve uncertainty, archive, supersede, explicit delete |
| Proposals | Submit, list, inspect, accept, reject |
| Immediate publication | Submit-and-publish, including bounded single-collection batches |
| Operations | Reindex, full verification, retrieval diagnostics, host health |

Contract requirements:

- Consistent logical identity and revision representation.
- Request validation and machine-readable errors.
- Distinguishable unauthorized/unsupported operation, revision conflict, missing target, invalid section, budget issue, and provider-unavailable outcomes.
- Stable pagination semantics for exact listing and relationship traversal.
- Explicit partial-result metadata.
- Cancellation and finite limits.
- Structured CLI output suitable for agents, plus concise human output.
- Protocol/retrieval version negotiation or clear unsupported-version errors.

Do not expose expensive maintenance operations to read-only API keys. Do not embed diagnostic verbosity in normal discovery responses.

## 13. Agent instructions and manual use

Replace manifest-reading guidance with a short operational workflow:

1. Use project configuration and a short task query; add relevant structured context.
2. Inspect the bounded discovery list.
3. Fetch needed entries or sections explicitly.
4. Follow entry references only when needed to understand/apply relevant content.
5. Treat cross-project material as reference, not governing instruction.
6. Recognize partial-source failures and applicability uncertainty.
7. Capture durable project knowledge in designated writable collections, preferring updates over duplicates.
8. Keep curated edits in proposals unless explicitly approved for submit-and-publish.
9. Never interpret silence as approval for curated publication.

Retain useful evidence, uncertainty, and curation discipline from the old scaffold. Do not retain automatic always-read rule loading or mandatory catalogue scanning.

Manual users must be able to execute every core workflow without agent tooling. Thin agent skills/commands may explain CLI/API usage but must not contain a separate implementation of retrieval or publishing.

## 14. Implementation sequence and deliverables

### Phase 1 — Contracts and foundation

Deliver:

- .NET solution and dependency boundaries.
- Domain schema for identities, entry metadata, applicability, lifecycle, revisions, references, and capabilities.
- Project and host configuration contracts, validation, path resolution, defaults, and credential references.
- Application interfaces for authoritative storage, indexing, providers, and operations.
- Versioned transport contracts and error model.
- Initial retrieval fixtures and expected relevance cases.

Complete before adapter implementation: exact required fields, schema version, identifier format, version-constraint representation, file matching semantics, and heading-anchor rules. Choose practical deterministic implementations; these details were not preselected in the interview.

### Phase 2 — Local end-to-end retrieval

Deliver:

- Markdown collection parsing and validation.
- SQLite FTS5 index and incremental indexing.
- CLI search/list/get and section retrieval.
- Candidate ranking, relevance admission, relaxation, aliases, and diagnostics.
- Result/response budgeting.
- Reference extraction, canonical links, outgoing references, and backlinks.
- Read-only package-root support and separate runtime directory.

Milestone: a user can query local project and package knowledge without loading a manifest or running a server.

### Phase 3 — Maintenance and authoritative database storage

Deliver:

- SQLite authoritative storage with adapter parity.
- Revision-checked direct maintenance and lifecycle actions.
- Proposal creation, inspection, acceptance, rejection, and audit records.
- Disabled-by-default submit-and-publish with target capabilities.
- Bounded single-collection batches.
- Atomic single-file Markdown writes, recoverable batch journal, recovery tooling.
- Explicit local proposals for non-writable sources.

Milestone: manual and automated maintenance work without silent overwrite or publishing bypasses.

### Phase 4 — HTTP and federation

Deliver:

- ASP.NET Core host and authentication/authorization.
- Remote HTTP provider and one-hop hosted search boundary.
- Per-source context forwarding and timeouts.
- Concurrent queries, partial failures, cancellation.
- Rank fusion and candidate overfetch.
- Identity deduplication, source preference, revision-disagreement reporting.
- Exact listing/get semantics across configured providers.

Milestone: one CLI invocation can combine local Markdown, local SQLite, and remote collections within the final response budget.

### Phase 5 — Evaluation, packaging, and repository replacement

Deliver:

- Reviewed retrieval corpus and calibrated defaults.
- Integration/failure/security/recovery checks appropriate to the implemented behaviors.
- Self-contained builds for Windows, macOS, and Linux.
- Installation, host deployment, configuration, schema, CLI, and API documentation.
- Concise agent usage and curated-approval instructions.
- Replacement of legacy manifest distribution and obsolete installers/workflows after the new end-to-end workflow works.

Inspect current repository status before removal. Preserve unrelated files and user-authored work, including local skills. Old behavior remains recoverable through Git history; no migration or dual-architecture maintenance is required.

## 15. Verification and acceptance criteria

### 15.1 Retrieval evaluation corpus

Create a small reviewed corpus with explicit expected relevant and irrelevant entries. Include:

- General clean-code guidance usable from Unity and web projects.
- Unity-only and web-only material.
- Same-project decisions and package-specific guidance.
- Strongly relevant other-project experience that should qualify.
- Weak other-project tag matches that should not qualify.
- Unknown and explicitly incompatible version applicability.
- No-match queries.
- Queries using abbreviations/aliases and exact identifiers.
- Relaxation variants on the same task.
- Competing results from differently sized collections.
- Identical logical entries from multiple sources and conflicting revisions.
- A poor top result from one source versus an excellent result from another.

Measure expected-entry retrieval, irrelevant-result rate, response bytes/estimated tokens, and latency. Establish measured baselines and acceptable thresholds during calibration; do not invent a latency SLA in documentation.

### 15.2 Functional acceptance

- No normal agent discovery step reads the full manifest/catalogue.
- CLI works manually and requires neither an AI service nor a local daemon.
- Markdown and SQLite adapters expose consistent semantics.
- Configured package directories can remain read-only.
- General knowledge works across technologies; project knowledge is penalized rather than categorically hidden.
- Relaxation does not bypass explicit filters, permissions, incompatibility exclusions, or budgets.
- Exact list pagination can enumerate all matching entries under documented consistency semantics.
- Get supports sections, explicit truncation, and bounded batch behavior.
- References survive file moves and resolve by identity through configured sources.
- Missing references are reported without network discovery or recursive expansion.
- Federation distinguishes no matches from partial failure.
- Duplicate identities are collapsed and disagreement remains visible.
- Complete response envelopes obey hard size limits.

### 15.3 Maintenance and authorization acceptance

- Read-only credentials/collections cannot write via any alternate endpoint.
- Curated publishing cannot bypass permissions through generic maintenance.
- Submit-and-publish is disabled by default and governed only by target enablement plus permissions.
- Agent instructions clearly require explicit approval for curated immediate publication.
- Project-memory automatic maintenance remains supported.
- Stale revisions are rejected, including externally edited Markdown.
- SQLite batch failure rolls back transactionally.
- Markdown interruption/recovery is tested at meaningful journal stages.
- Local proposal storage does not impersonate or mutate a read-only source.
- Archive/supersession states affect default search and remain inspectable explicitly.
- Key revocation, collection scoping, and context-forwarding restrictions work.

### 15.4 Index and delivery acceptance

- Changes, deletions, moves, and reference edits update the index correctly.
- Reindex reconstructs derived state from authoritative content.
- CLI incremental checks avoid reparsing every unchanged body.
- Host reconciliation recovers missed filesystem notifications.
- Self-contained binaries are exercised on supported platforms or any unverified platform is clearly reported.
- Obsolete manifest instructions do not remain active alongside the new workflow.

Prefer meaningful contract/integration/evaluation tests over tests that merely mirror implementation. Run checks appropriate to each milestone and report actual verification results honestly.

## 16. Implementation cautions and unresolved mechanics

The product decisions are settled. The following engineering mechanics still require explicit implementation choices and documentation, rather than silent assumptions:

- Exact config filename, runtime directory name, command names, routes, and DTO field names.
- Stable ID format, schema versions, and compatibility handling.
- Frontmatter schema and parser/library choices.
- Version-range syntax across Unity and other packages; unknown values must not be guessed compatible.
- File association/glob semantics and symbol matching.
- FTS query escaping, language/tokenization behavior, alias precedence, and malicious/malformed query handling.
- Ranking weights, relevance thresholds, fusion formula, tie-breaking, and duplicate-replica influence.
- Response token-estimation method and minimum legal budget.
- Federated exact-list pagination and consistency under changing sources.
- Canonical revision derivation/comparison across adapters and source copies.
- Markdown locking, crash recovery, index-write recovery, and audit retention.
- Section boundaries, heading anchors, and duplicate heading behavior.
- API key bootstrap/rotation procedures and distribution packaging details.

Resolve these within the agreed boundaries, recording consequential tradeoffs. If a choice would alter a settled requirement, ask the owner rather than silently narrowing scope.

## 17. Final decision checklist

- [x] Personal-first product; remote scoped API keys.
- [x] Abstract storage; Markdown and SQLite initially.
- [x] Separate storage, index, and remote provider concerns.
- [x] Cross-project retrieval via strong penalties and evidence-based admission.
- [x] No rules or always-read/bootstrap behavior.
- [x] Standalone manual operation; no required AI models.
- [x] Project configuration only; configurable query limits and relaxation.
- [x] CLI direct execution; optional HTTP hosting; no Unity wrapper here.
- [x] Stable entry and collection identity.
- [x] Rank fusion, bounded overfetch, identity deduplication, explicit revision conflicts.
- [x] Full/section/batch get; exact paginated list separate from ranked search.
- [x] Incremental indexing with explicit reindex and host reconciliation.
- [x] Curated proposals plus explicitly approved submit-and-publish.
- [x] Target-controlled immediate publication; no redundant caller enablement.
- [x] Revision checks, single-collection batches, recoverable Markdown journaling.
- [x] Archive/supersession lifecycle, explicit deletion, no age-only retirement.
- [x] Explicit aliases, centrally defined scoring, optional diagnostics.
- [x] Read-only package capabilities and explicit local proposals.
- [x] One-hop federation with controlled context forwarding and partial results.
- [x] .NET 10 LTS, ASP.NET Core, SQLite FTS5, self-contained cross-platform distribution.
- [x] New primary tool; no migration; retire old scaffold after working replacement.
- [x] Markdown identity links, sections, backlinks, explicit fetching, no recursive expansion.
- [x] Current-content references; historical pinning deferred.
- [x] Evaluation-driven relevance and efficiency acceptance.
