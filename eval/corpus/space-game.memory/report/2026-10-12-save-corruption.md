---
schema: 1
id: 2026-10-12-save-corruption
kind: report
title: "Save corruption after crash during save"
summary: "Investigation of truncated slot files after a crash mid-write; led to atomic temp-file writes."
tags: [save, investigation, crash]
files: ["Assets/Scripts/Save/**"]
---
# Investigation

Players reported empty slots after crashes. Writes went straight to `slot-N.json`; a crash left a
truncated file. Fixed by writing to a temp file and renaming.
