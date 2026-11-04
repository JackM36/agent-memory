---
schema: 1
id: validate-boundaries
kind: knowledge
title: "Validate external data at the boundary with zod"
summary: "Parse API responses and form input with a schema at the edge so the rest of the code can trust its types."
tags: [validation, typescript, api]
symbols: [z.object, safeParse]
---
# Validating at boundaries

TypeScript types disappear at runtime. Parse once at the boundary with `schema.safeParse(json)`
and pass the typed result inward.
