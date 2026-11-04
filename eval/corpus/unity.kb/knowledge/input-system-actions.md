---
schema: 1
id: input-system-actions
kind: knowledge
title: "Read input through Input System actions, not devices"
summary: "Bind gameplay to InputActions so keyboard, gamepad and touch share one code path and players can rebind."
tags: [input, controls]
symbols: [InputAction, PlayerInput]
applies:
  packages:
    - { name: com.unity.inputsystem, versions: ">=1.0" }
---
# Input actions

Define actions in an Input Actions asset and read `action.ReadValue<Vector2>()`. Avoid polling
`Gamepad.current` directly in gameplay code.
