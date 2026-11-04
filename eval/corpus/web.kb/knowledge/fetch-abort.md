---
schema: 1
id: fetch-abort
kind: knowledge
title: "Cancel stale requests with AbortController"
summary: "Abort the previous fetch when inputs change so out-of-order responses cannot overwrite newer results."
tags: [networking, async, race-conditions]
symbols: [AbortController, fetch]
---
# Aborting fetches

Create an `AbortController` per request, pass `signal` to `fetch`, and abort it in the effect
cleanup or before starting the next search-as-you-type request.
