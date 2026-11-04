---
schema: 1
id: schema-versioning
kind: knowledge
title: "Version persisted data formats explicitly"
summary: "Store a format version with every persisted document and migrate old versions step by step on load."
tags: [serialization, versioning, persistence, migration]
---
# Versioning persisted formats

Write a version number into every saved document. On load, run an ordered chain of migrations
(v1 to v2, v2 to v3) until the document is current, then validate it.

## Rules

- Never infer the version from the shape of the data.
- Keep migrations pure functions of the old document.
- Test every migration with a stored fixture from the old version.
