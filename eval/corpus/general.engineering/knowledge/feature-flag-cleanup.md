---
schema: 1
id: feature-flag-cleanup
kind: knowledge
title: "Remove feature flags once a rollout completes"
summary: "Every feature flag needs an owner and a removal date; stale flags multiply untested code paths."
tags: [feature-flags, maintenance]
---
# Feature flag cleanup

Create the removal task when you create the flag. A flag left on everywhere for a month is dead
configuration that hides a second, untested code path.
