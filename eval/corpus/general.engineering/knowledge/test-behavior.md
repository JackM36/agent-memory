---
schema: 1
id: test-behavior
kind: knowledge
title: "Test behaviour through public contracts, not implementation"
summary: "Assert on observable outcomes of the public API so refactors do not break tests that still describe correct behaviour."
tags: [testing, refactoring]
---
# Testing behaviour

Tests that mirror private structure fail on every refactor and pass when behaviour is wrong.
Drive tests through the public operation and assert on results, emitted events or persisted state.
