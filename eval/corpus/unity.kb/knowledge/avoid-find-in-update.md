---
schema: 1
id: avoid-find-in-update
kind: knowledge
title: "Avoid GameObject.Find and GetComponent in Update"
summary: "Cache component and object lookups in Awake or Start; per-frame lookups are slow and allocate."
tags: [performance, components]
symbols: [GameObject.Find, GetComponent, Update]
---
# Cache lookups

`GameObject.Find` walks the whole scene. Resolve references once in `Awake`, or wire them in the
inspector, and keep `Update` free of searches.
