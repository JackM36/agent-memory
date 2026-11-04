---
schema: 1
id: react-effect-deps
kind: knowledge
title: "Declare every value used in useEffect as a dependency"
summary: "Missing useEffect dependencies cause stale values; fix the dependency list instead of suppressing the lint rule."
tags: [react, hooks, stale-closure]
symbols: [useEffect, useCallback]
applies:
  technologies: [web, typescript, react]
---
# Effect dependencies

An effect captures the values from the render it was created in. If it reads a prop or state that
is not in the dependency array it keeps seeing the old value. In development, React mounts effects
twice to surface missing cleanup.
