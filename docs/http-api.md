# HTTP API (protocol 1)

`brain-server` exposes the same operations as the CLI and MCP server. Every rule (validation, revision
checks, secret scanning, admission, budgets) lives in the shared application layer, so a remote
collection behaves exactly like a local one. Clients normally use `brain` with a configured
connection (see [deployment.md](deployment.md)). This page is for people writing other clients or
debugging.

## Conventions

- **Authentication.** Every `/v1` request needs `Authorization: Bearer brain_<keyId>_<secret>`. A missing,
  wrong or revoked key gets `401`. A key without the needed scope gets `403`; collections it cannot
  read are simply invisible.
- **Protocol.** Clients may send `Agent-Brain-Protocol: 1`. A different value gets `400
  protocol_version_unsupported`. `/health` reports the server's protocol.
- **Bodies.** UTF-8 JSON with camelCase names, at most 1 MiB. Queries are at most 1,000 characters.
- **Identities.** Entries are addressed as `{collection}/{entry}`: lowercase slugs, validated before any
  storage access. Malformed identities are `400` or `404` and never reach the file system.
- **Revisions.** Updates and deletes carry the `baseRevision` the client read. A mismatch is `409
  revision_conflict` with `details.currentRevision`.
- **Transport.** Behind a proxy, `/v1` requests that did not arrive over HTTPS are refused with `403`.

## Errors

```json
{ "protocol": 1, "code": "revision_conflict", "message": "…", "details": { "currentRevision": "…" } }
```

| Status | Codes |
|---|---|
| 400 | `validation_failed`, `config_invalid`, `protocol_version_unsupported`, `budget_too_small` |
| 401 | `unauthorized` |
| 403 | `forbidden`, `read_only`, `unsupported_operation` |
| 404 | `not_found`, `section_not_found` |
| 409 | `revision_conflict`, `lock_timeout` |
| 422 | `secret_detected` |
| 503 | `provider_unavailable`, `index_unavailable` |
| 500 | `internal` |

## Routes

| Method and path | Scope | Purpose |
|---|---|---|
| `GET /health` | none | `{ "status": "ok", "protocol": 1, "version": "…" }` |
| `GET /v1/collections` | read | Collections the key can read, with descriptors |
| `POST /v1/search` | read | Ranked, budgeted search |
| `POST /v1/list` | read | Exact listing with filters and a cursor |
| `GET /v1/entries/{c}/{e}` | read | One entry; `?section=<anchor>`, `?maxBytes=`, `?withRefs=true` |
| `POST /v1/entries:batchGet` | read | `{ "refs": ["c/e", …], "maxBytes": n, "withReferences": bool }` |
| `GET /v1/entries/{c}/{e}/references` | read | Outgoing `entry://` links, resolved |
| `GET /v1/entries/{c}/{e}/backlinks` | read | Entries linking here; `?pageSize=`, `?cursor=` |
| `POST /v1/entries` | maintain | Create (body: a change with `kind: "create"`) |
| `PUT /v1/entries/{c}/{e}` | maintain | Update |
| `POST /v1/entries/{c}/{e}/archive` | maintain | Archive |
| `POST /v1/entries/{c}/{e}/supersede` | maintain | Supersede (`supersededBy`) |
| `POST /v1/entries/{c}/{e}/resolve` | maintain | Resolve an uncertainty (`resolvedBy`) |
| `DELETE /v1/entries/{c}/{e}?baseRevision=` | maintain | Delete |
| `POST /v1/proposals` | propose | Submit a change for review |
| `GET /v1/proposals` | read | Proposals for collections the key can read; `?status=pending\|accepted\|rejected` |
| `GET /v1/proposals/{id}` | read | One proposal, with its body |
| `POST /v1/proposals/{id}/accept` | publish | Apply it (optional `{ "note": "…" }`) |
| `POST /v1/proposals/{id}/reject` | publish | Reject it |
| `POST /v1/publish` | publish | `{ "changes": [ … ] }`; atomic on SQLite collections |
| `POST /v1/admin/reindex` | admin | Incremental index refresh |
| `POST /v1/admin/verify` | admin | Full verification of index against storage |

Scopes are granted per collection (`collection:<id>:<capability>`, or `*` for all collections) and are
checked against the target collection of each request. A read-only collection refuses writes whatever
the key's scopes.

## Request bodies

### Search

```json
{
  "query": "retry with backoff",
  "context": { "project": "space-game", "technologies": ["unity"], "packages": { "com.unity.entities": "1.2.0" },
               "files": ["Assets/Save/SaveSystem.cs"], "symbols": ["SaveSystem"], "hintTags": [] },
  "filters": { "kinds": ["knowledge"], "tags": ["networking"], "tagsAny": false, "collections": ["team"] },
  "relaxation": "default",
  "maxResults": 10, "maxBytes": 8000, "maxTokens": null,
  "includeHistory": false, "includeIncompatible": false, "explain": false
}
```

The server caps `maxResults` and `maxBytes` at the host's `limits`. The response contains `results`,
`blocking` (blocking uncertainties for the context), `sources`, `indexStale`, `byteLimit` and
`usedBytes`. The schema is in [schema/](schema/).

The client decides which context to forward (`forward` on the connection). Nothing is sent by default.

### Changes

Maintenance, proposals and publish batches all use one shape:

```json
{
  "kind": "update", "collection": "team", "entry": "retry-policy", "baseRevision": "…",
  "entryKind": "knowledge", "title": "…", "summary": "…", "body": "…",
  "tags": [], "files": [], "symbols": [], "evidence": [],
  "archive": false, "supersededBy": null, "resolvedBy": null, "blocking": false, "note": "why"
}
```

Omitted fields in an update keep their current value. Bodies are scanned for secrets before anything is
stored (`422 secret_detected`). A create also runs the similar-entry check and lists likely duplicates
in its response (`similar`), so the client can merge or archive one.

## Versioning

The protocol number changes only for incompatible changes. New optional fields and new routes do not
change it. A client must ignore unknown response fields.
