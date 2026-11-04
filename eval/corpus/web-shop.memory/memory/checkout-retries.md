---
schema: 1
id: checkout-retries
kind: memory
title: "Payment calls retry only with an idempotency key"
summary: "Checkout retries payment requests only when they carry an idempotency key; other calls fail fast."
tags: [payments, resilience, retries]
symbols: [createPaymentIntent]
---
# Payment retries

A duplicate charge in 2026-03 came from retrying without an idempotency key.
