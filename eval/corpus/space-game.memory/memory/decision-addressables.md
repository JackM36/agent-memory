---
schema: 1
id: decision-addressables
kind: memory
title: "Decision: load levels through Addressables, not Resources"
summary: "Levels and large assets load asynchronously via Addressables labels; the Resources folder is reserved for boot assets."
tags: [assets, loading, levels, decision]
files: ["Assets/Scripts/Levels/**"]
symbols: [LevelLoader]
applies:
  packages:
    - { name: com.unity.addressables, versions: ">=1.21" }
evidence: ["user decision 2026-09-20"]
---
# Addressables for levels

`LevelLoader` loads scenes with `Addressables.LoadSceneAsync` using the `level` label and releases
the previous level handle after the transition.
