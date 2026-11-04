---
schema: 1
id: save-format-v2-undecided
kind: uncertainty
title: "Save-file versioning strategy is undecided"
summary: "Ask before changing SaveSystem serialization: migration vs. wipe for old saves has not been decided."
tags: [save, versioning, migration]
files: ["Assets/Scripts/Save/**"]
symbols: [SaveSystem]
resolution: unresolved
blocking: true
---
# Question

When the save format changes, do we migrate old saves or invalidate them?

## Why this matters

Players lose progress if an agent picks "wipe" silently.
