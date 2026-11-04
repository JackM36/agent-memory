---
schema: 1
id: structured-logging
kind: knowledge
title: "Log structured events, not formatted sentences"
summary: "Emit log events with named fields so they can be filtered and aggregated; keep message templates constant."
tags: [logging, observability]
---
# Structured logging

Use message templates with named placeholders and keep the template constant across calls.
Never log secrets, tokens or full personal data.
