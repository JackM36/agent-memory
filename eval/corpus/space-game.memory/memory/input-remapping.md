---
schema: 1
id: input-remapping
kind: memory
title: "Players can remap gamepad and keyboard bindings"
summary: "Rebinding uses the Input System interactive rebinding UI; overrides are saved as JSON in PlayerPrefs."
tags: [input, controls, settings]
symbols: [RebindActionUI, PlayerInput]
evidence: ["user confirmed 2026-10-02"]
---
# Input remapping

`RebindActionUI` wraps `PerformInteractiveRebinding`. Binding overrides are saved with
`SaveBindingOverridesAsJson`.
