---
schema: 1
id: node-graceful-shutdown
kind: knowledge
title: "Shut Node.js servers down gracefully on SIGTERM"
summary: "On SIGTERM stop accepting connections, finish in-flight requests, close pools, then exit before the orchestrator kills the process."
tags: [reliability, deployment, signals]
symbols: [process.on, server.close]
applies:
  technologies: [web, node.js]
---
# Graceful shutdown

Container platforms send SIGTERM and wait a grace period before SIGKILL. Handle the signal, call
`server.close()`, drain work, and set a hard timeout as a fallback.
