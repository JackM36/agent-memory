---
schema: 1
id: image-cdn
kind: memory
title: "Product images are resized by the CDN"
summary: "Request image sizes through CDN query parameters; never ship original uploads to the browser."
tags: [images, performance, cdn]
---
# Images

Use the `<ProductImage>` component, which builds the CDN URL with width and format parameters.
