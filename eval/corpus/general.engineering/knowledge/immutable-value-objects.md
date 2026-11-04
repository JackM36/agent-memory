---
schema: 1
id: immutable-value-objects
kind: knowledge
title: "Model values as immutable value objects"
summary: "Wrap domain values like money or IDs in small immutable types that validate on construction."
tags: [domain-modeling, immutability, validation]
---
# Immutable value objects

Validate once in the constructor, never mutate afterwards, and compare by value. This removes a
whole class of "who changed this?" bugs and makes invalid states unrepresentable.
