---
schema: 1
id: react-query-cache
kind: memory
title: "Server state is cached with TanStack Query"
summary: "All server data goes through TanStack Query with a 30 second stale time; no server data in Redux."
tags: [caching, react, state]
symbols: [useQuery, QueryClient]
---
# Server state

Mutations invalidate by query key prefix.
