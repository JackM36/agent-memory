---
schema: 1
id: guard-clauses
kind: knowledge
title: "Prefer guard clauses over nested conditionals"
summary: "Return early for invalid or trivial cases so the main path stays flat and readable."
tags: [readability, control-flow, refactoring]
verified: { on: 2026-09-01, by: repeated-use }
---
# Guard clauses

Check preconditions at the top of a function and return or throw immediately. The remaining code
handles only the normal case, without extra indentation.

## When not to

When every branch is equally important (a state machine, a parser), a `switch` or a table of
handlers communicates intent better than a chain of early returns.
