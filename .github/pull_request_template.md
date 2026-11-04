<!--
The PR title becomes the squash commit and decides the release:
  feat: …   → minor     fix: … / perf: … / build: … / revert: … → patch
  feat!: … or "BREAKING CHANGE:" in the body → major
  docs: … / chore: … / ci: … / refactor: … / style: … / test: … → no release
-->

## Summary

Describe the change in one or two sentences.

## Testing

List the checks run and their results.

## Upgrade notes

Configuration, file-format, protocol or integration changes users must act on (for example "run brain init
again"), or write `None`.
