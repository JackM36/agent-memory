---
schema: 1
id: naming-intent
kind: knowledge
title: "Name things by intent, not by type"
summary: "Names should say what a value means to the reader (retryDelay), not how it is stored (intValue)."
tags: [naming, readability]
---
# Naming by intent

- Prefer `pendingOrders` to `orderList`.
- Booleans read as questions: `isExpired`, `hasAccess`.
- Avoid abbreviations unless the domain uses them everywhere.
