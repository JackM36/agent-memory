---
schema: 1
id: jsonutility-limitations
kind: knowledge
title: "JsonUtility limitations for save data"
summary: "JsonUtility cannot serialise dictionaries, top-level arrays, properties or polymorphic types; wrap or switch serialisers."
tags: [serialization, json, save-data]
symbols: [JsonUtility, JsonUtility.ToJson, JsonUtility.FromJson]
---
# JsonUtility limitations

- No `Dictionary<,>`: store a list of key/value pairs instead.
- No top-level arrays: wrap them in a class.
- Only public fields or `[SerializeField]` fields, never properties.
- No polymorphism.

For save files that must evolve, combine a wrapper type with an explicit format version.
