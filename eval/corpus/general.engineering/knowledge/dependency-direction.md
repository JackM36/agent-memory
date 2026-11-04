---
schema: 1
id: dependency-direction
kind: knowledge
title: "Point dependencies toward stable abstractions"
summary: "Apply the dependency inversion principle: policy code depends on interfaces it owns, details implement them."
tags: [architecture, dependency-inversion, layering]
---
# Dependency direction

High-level policy should not import low-level details (database drivers, HTTP clients, engines).
Define the interface next to the policy that needs it and let infrastructure implement it.

## Symptoms of a violation

- Unit tests need a real database or engine running.
- Changing a storage library forces edits in business rules.
