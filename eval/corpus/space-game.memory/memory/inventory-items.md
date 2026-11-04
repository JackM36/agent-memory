---
schema: 1
id: inventory-items
kind: memory
title: "Inventory items are ScriptableObject definitions keyed by GUID"
summary: "Each item type is an ItemDefinition asset with a stable GUID; saves store GUIDs, never asset paths."
tags: [inventory, scriptableobject, items]
files: ["Assets/Scripts/Inventory/**", "Assets/Data/Items/**"]
symbols: [ItemDefinition, ItemDatabase]
---
# Inventory items

Add a new item type by creating an `ItemDefinition` asset under `Assets/Data/Items`. The
`ItemDatabase` builds a GUID lookup at startup; saves reference items by GUID.
