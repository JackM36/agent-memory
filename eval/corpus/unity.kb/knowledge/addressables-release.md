---
schema: 1
id: addressables-release
kind: knowledge
title: "Release Addressables handles to avoid memory leaks"
summary: "Every Addressables load or instantiate must be paired with a Release; leaked handles keep bundles in memory."
tags: [memory, assets, loading]
symbols: [Addressables.LoadAssetAsync, Addressables.Release, Addressables.ReleaseInstance]
applies:
  packages:
    - { name: com.unity.addressables, versions: ">=1.19" }
---
# Releasing Addressables

Track every handle you load. Instances created with `InstantiateAsync` are released with
`ReleaseInstance`; destroying the GameObject alone leaks the bundle reference count.
