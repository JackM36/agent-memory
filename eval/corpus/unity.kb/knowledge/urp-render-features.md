---
schema: 1
id: urp-render-features
kind: knowledge
title: "Custom URP renderer features"
summary: "Add custom passes to the Universal Render Pipeline with a ScriptableRendererFeature and a ScriptableRenderPass."
tags: [rendering, urp, scriptable-render-pipeline]
symbols: [ScriptableRendererFeature, ScriptableRenderPass]
applies:
  packages:
    - { name: com.unity.render-pipelines.universal, versions: ">=12.0" }
---
# Renderer features

URP is a scriptable render pipeline: extend it by adding a renderer feature to the renderer asset.
From URP 17, write passes with the render graph API.
