---
schema: 1
id: projectile-collisions
kind: memory
title: "Projectiles ignore the Player layer via the collision matrix"
summary: "Player projectiles do not collide with the Player layer; this is set in the physics layer collision matrix, not in code."
tags: [physics, collisions, gotcha]
symbols: [Projectile]
---
# Projectile collisions

If shots pass through something unexpectedly, check Project Settings, Physics, Layer Collision
Matrix before debugging `Projectile.OnTriggerEnter`.
