---
schema: 1
id: small-functions
kind: knowledge
title: "Keep functions small and single-purpose"
summary: "A function should do one thing at one level of abstraction; extract steps that need a comment to explain them."
tags: [readability, refactoring, functions]
---
# Small functions

If a block needs a comment to say what it does, extract it and let the function name say it.
Mixing levels of abstraction (business rule next to string parsing) is the usual signal.

See [guard clauses](entry://general.engineering/guard-clauses) for keeping the main path flat.
