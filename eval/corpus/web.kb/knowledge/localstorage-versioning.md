---
schema: 1
id: localstorage-versioning
kind: knowledge
title: "Version data stored in localStorage"
summary: "Store a version field with every localStorage payload and migrate or discard old shapes on read."
tags: [storage, versioning, serialization, json]
symbols: [localStorage]
---
# localStorage versioning

Wrap payloads as `{ v: 3, data }`. Parse defensively: storage can hold values written by any
older release, or be edited by the user.
