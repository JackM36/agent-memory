# 0009 — Releases, installation and upgrades

- **Status:** accepted; the release workflow has not run yet
- **Date:** 2026-11-04
- **Plan:** replaces the tag-triggered release in §9.7

## Decisions

- **Releases are decided by merged PR titles, as in AppDrop.**
  - Pull requests into protected `main` are squash-merged with Conventional Commit titles. PR Validation
    rejects other titles and `[skip ci]`.
  - On each push to `main`, `semantic-release --dry-run` plans the version. A small plugin writes the plan
    to `release-plan.json`, so the artifacts are built for that version before anything is tagged.
  - The real run then tags and publishes. The workflow checks that the tag points at the built commit and
    only then moves the image's `latest`.
  - Version rules and plugin versions are copied from AppDrop: `feat` → minor; `fix`, `perf`, `build`,
    `revert` → patch; breaking → major; the rest → no release.
- **Node is used only for release tooling.** `package.json` holds pinned semantic-release packages, nothing
  else. The product stays .NET. The tooling has its own tests (`npm test`), run in PR Validation and before
  every release.
- **The version comes from the release.**
  - `Directory.Build.props` defaults to `0.0.0-dev`. CI passes `-p:Version`, and the Docker build gets
    `VERSION` as a build argument.
  - The repository that `brain upgrade` reads is embedded at build time (`AgentBrainRepository`). It can
    be overridden with `AGENT_BRAIN_REPOSITORY`.
- **Asset names carry no version** (`brain-<rid>.zip|tar.gz`, `brain-server-<rid>…`, `SHA256SUMS`), so
  `releases/latest/download/<name>` is a stable URL.
- **Install scripts instead of package managers for now.**
  - `install.ps1` and `install.sh` install to one fixed user folder per OS and add it to `PATH`. No admin
    rights are needed.
  - The fixed folder matters: MCP registrations and hooks written by `brain init` call `brain`, and they
    must survive upgrades.
  - Scoop, Homebrew or winget can be added later and fed by the same release assets.
- **`brain upgrade`, not `brain update`.** `update` already means updating an entry.
  - The upgrade reads the GitHub releases API, downloads this platform's archive, verifies it against
    `SHA256SUMS`, and swaps the files.
  - On Windows a running `brain.exe` (an agent's MCP server) cannot be overwritten but can be renamed, so
    old files are moved aside as `.old` and deleted on the next upgrade.
  - Any failure puts the swapped files back.
  - An optional `AGENT_BRAIN_GITHUB_TOKEN` is sent to the API only, never with downloads.
- **`brain doctor` reports newer releases.** The check has a 3-second limit, never fails the doctor, and
  can be disabled with `AGENT_BRAIN_NO_UPDATE_CHECK`. Plain commands and `brain mcp` never check.
- **The server image is pinned by version** in `deploy/.env`. Upgrade or roll back by changing the
  version and running `docker compose pull && up -d`.

## Verified locally

| Check | Result |
|---|---|
| A versioned AOT build | Reports `0.2.0+<sha>` |
| `install.ps1` against a locally served release | Fresh install and reinstall over an existing install both work; a tampered `SHA256SUMS` is refused and nothing is installed |
| `install.sh` | Parses under `dash`; its checksum lookup matches exact names, with and without the `*` binary marker |
| `brain upgrade` (unit tests against a fake GitHub) | Zip and tar installs, the files moved aside and cleaned up, a checksum mismatch, a missing release or asset, the token sent to the API only |

## Not verified

- **The workflows** have not run on GitHub; the first merged `feat:` PR will be their first run.
- **The arm64 and Intel-Mac runners** (`windows-11-arm`, `ubuntu-24.04-arm`, `macos-13`) are used as
  configured, but not yet tried.
- **`install.sh`** has not run end to end on macOS or Linux.
