---
schema: 1
id: scriptableobject-config
kind: knowledge
title: "Use ScriptableObjects for shared configuration data"
summary: "Keep tuning data and definitions in ScriptableObject assets instead of duplicating it on prefabs."
tags: [architecture, data, scriptableobject]
symbols: [ScriptableObject, CreateAssetMenu]
---
# ScriptableObject configuration

Assets created with `[CreateAssetMenu]` are shared by reference, edited in the inspector and diffed
in version control. Do not store runtime state in them: changes made in Play Mode persist in the editor.
