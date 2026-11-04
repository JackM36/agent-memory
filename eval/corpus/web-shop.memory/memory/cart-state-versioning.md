---
schema: 1
id: cart-state-versioning
kind: memory
title: "Saved cart state is versioned with a migration chain"
summary: "Persisted cart state carries a version; on load an ordered chain of migrations upgrades old saved data before validation."
tags: [versioning, migration, persistence, serialization]
files: ["src/cart/persist.ts"]
symbols: [migrateCart, CART_VERSION]
evidence: ["src/cart/persist.ts", "incident 2026-05: old carts crashed checkout"]
---
# Cart state versioning

After a release changed the cart shape, old saved carts crashed checkout. Now every save writes
`CART_VERSION` and `migrateCart` applies v1 to v2 to v3 migrations, each tested with a stored fixture.
Unknown future versions are discarded rather than guessed.
