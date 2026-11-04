---
schema: 1
id: hud-ui-toolkit
kind: memory
title: "HUD uses UI Toolkit; menus still use uGUI"
summary: "The in-game HUD is built with UI Toolkit; pause and main menus remain uGUI until the menu rewrite."
tags: [ui, hud]
files: ["Assets/UI/**"]
---
# UI split

Do not add new uGUI elements to the HUD. Menus are scheduled for migration after release.
