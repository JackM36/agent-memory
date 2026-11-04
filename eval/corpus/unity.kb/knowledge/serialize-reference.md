---
schema: 1
id: serialize-reference
kind: knowledge
title: "When [SerializeReference] is required"
summary: "Unity only serialises polymorphic or null-able managed references in fields marked [SerializeReference]."
tags: [serialization, polymorphism, inspector]
symbols: [SerializeReference]
applies:
  packages:
    - { name: unity, versions: ">=2019.3" }
---
# SerializeReference

Plain `[SerializeField]` serialises by value: a `List<Weapon>` holding `Sword` objects comes back
as base `Weapon` instances. Mark the field `[SerializeReference]` to keep the concrete type.

## Managed references

Renaming or moving a referenced class breaks existing data unless you add `[MovedFrom]`.

## Pitfalls

- No support for `UnityEngine.Object` types.
- Generic types need Unity 2023.1 or later.
