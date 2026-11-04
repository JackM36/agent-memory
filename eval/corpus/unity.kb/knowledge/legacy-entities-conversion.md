---
schema: 1
id: legacy-entities-conversion
kind: knowledge
title: "GameObject conversion workflow (Entities 0.x)"
summary: "Entities 0.x converted GameObjects with IConvertGameObjectToEntity and ConvertToEntity; removed in 1.0."
tags: [ecs, conversion, dots]
symbols: [IConvertGameObjectToEntity, ConvertToEntity]
applies:
  packages:
    - { name: com.unity.entities, versions: "<1.0" }
---
# Legacy conversion

Implement `IConvertGameObjectToEntity.Convert` and add `ConvertToEntity` to the GameObject.
This workflow does not exist in Entities 1.0 and later.
