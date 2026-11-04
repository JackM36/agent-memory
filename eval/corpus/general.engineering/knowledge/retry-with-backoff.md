---
schema: 1
id: retry-with-backoff
kind: knowledge
title: "Retry transient failures with exponential backoff and jitter"
summary: "Retry only idempotent operations on transient errors, doubling the delay each attempt and adding random jitter."
tags: [networking, resilience, retries]
---
# Retry with backoff

Retrying immediately turns a brief outage into a thundering herd. Use exponential backoff with a
cap and full jitter, and give up after a bounded number of attempts.

Only retry operations that are safe to repeat (idempotent) or that carry an idempotency key.
