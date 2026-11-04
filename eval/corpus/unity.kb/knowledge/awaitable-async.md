---
schema: 1
id: awaitable-async
kind: knowledge
title: "Use Awaitable for async code instead of coroutines"
summary: "Unity 2023.1+ provides Awaitable for allocation-free async/await on the main thread, replacing most coroutines."
tags: [async, coroutines]
symbols: [Awaitable, Awaitable.NextFrameAsync, StartCoroutine]
applies:
  packages:
    - { name: unity, versions: ">=2023.1" }
---
# Awaitable

`await Awaitable.NextFrameAsync()` replaces `yield return null`, and cancellation works through
`CancellationToken` instead of `StopCoroutine`.
