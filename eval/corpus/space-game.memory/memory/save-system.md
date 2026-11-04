---
schema: 1
id: save-system
kind: memory
title: "Save system: slots, JSON payloads and atomic writes"
summary: "Saves are per-slot JSON files written atomically by SaveSystem; slot metadata lives in a separate index file."
tags: [save, serialization, persistence]
files: ["Assets/Scripts/Save/**"]
symbols: [SaveSystem, SaveSlot, SaveSystem.WriteSlot]
evidence: ["Assets/Scripts/Save/SaveSystem.cs", "user confirmed 2026-10-14"]
verified: { on: 2026-10-14, by: code }
---
# Save system

`SaveSystem.WriteSlot` serialises the game state with Newtonsoft JSON, writes `slot-N.json.tmp`,
then renames it over `slot-N.json`. `slots.json` holds display names and timestamps.

## Gotchas

- Never write a slot from a background thread: Unity objects are read during serialisation.
- The format has no version number yet. See [the open question](entry://space-game.memory/save-format-v2-undecided).
