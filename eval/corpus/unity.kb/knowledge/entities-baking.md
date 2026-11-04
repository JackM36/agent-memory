---
schema: 1
id: entities-baking
kind: knowledge
title: "Bake authoring GameObjects into ECS components"
summary: "In Entities 1.x, authoring MonoBehaviours are converted by Baker classes in subscenes, not at runtime."
tags: [ecs, baking, dots]
symbols: [Baker, IComponentData, GetEntity]
applies:
  packages:
    - { name: com.unity.entities, versions: ">=1.0 <2.0" }
---
# Baking

Write a `Baker<TAuthoring>` per authoring component and add components with `AddComponent`.
Baking runs in the editor when the subscene changes; runtime code only sees entities.
