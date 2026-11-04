# Operations

## Project layout

```text
.agent-brain/
├─ config.json            committed configuration
├─ config.local.json      Git-ignored overrides
├─ memory/                project-memory collection
├─ proposals/             proposals (committed, reviewable in Git)
└─ local/                 Git-ignored
   ├─ cache/index.db      search index — rebuildable, safe to delete
   └─ state/              durable local state
      ├─ locks/           cross-process locks (released automatically if a process dies)
      ├─ batches/         markers of interrupted publications
      ├─ audit/           proposal and publishing audit log (YYYY-MM.jsonl)
      └─ query-log.jsonl  opt-in search log
```

## The index

Every read first checks the collections for changes: file sizes and modification times are compared, and
only changed files are hashed and re-parsed. If another process is refreshing, a read waits 750 ms and then
uses the index as it is and reports it `STALE`.

| Command | Use |
|---|---|
| `brain index status` | Entries, files, invalid files with reasons |
| `brain index reindex` | Refresh changed files now |
| `brain index verify` | Hash every file (catches changes that kept size and time) |
| `brain index reset` | Rebuild from the collections |

Invalid files never break the index: they are listed with their problem and skipped. Two files with the same
entry ID: the first path (in ordinal order) wins, the other is reported as `duplicate_id`.

Measured on Windows (ADR 0006): at 10,000 entries a refresh with no changes takes ~40 ms, one edit ~50 ms, a
search ~44 ms and a full rebuild ~2.3 s.

## Writes and concurrency

- Every update and delete names the revision it was based on. If the file changed — through Agent Brain or
  by hand — the write is rejected with `revision_conflict` and the current revision.
- Files are written to a temporary file in the same directory, flushed, then renamed over the target. A
  crash never leaves a half-written entry.
- On Windows, a rename fails while another program holds the file open; Agent Brain retries for one second,
  then reports `lock_timeout` instead of writing in place.
- Agent Brain processes coordinate through lock files. External editors are not coordinated; the revision
  check catches their changes.

## Interrupted batches

A multi-entry publication to a Markdown collection writes a marker in `local/state/batches/` first and
removes it at the end. If a marker remains:

```bash
brain batch status
```

lists each change as `applied`, `pending` or `conflict`. `brain batch resume <id>` applies the pending ones
(each still checked against its base revision); `brain batch discard <id>` forgets the marker after you
restored the files yourself, for example with Git. Collections stored in SQLite (servers) apply batches in
one transaction instead.

Ordinary filesystems give no cross-file atomicity: during a batch, readers may briefly see some entries
updated and others not.

## Proposals and audit

Proposals live in `.agent-brain/proposals/<id>.json` and record the target, its base revision, the full
proposed content and who proposed it. Accepting re-checks permissions, validation, secrets and the base
revision. Audit records in `local/state/audit/` note who submitted, accepted, rejected or published what;
they are not content snapshots.
