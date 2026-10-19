---
name: native-aot-photo-library
description: "Build a folder-based WinUI 3 photo organizer and editor that publishes and launches with .NET Native AOT"
type: new
app_name: AotPhotoLibrary
gates:
  - native_aot
requirements:
  - "The app must allow the user to add one or more folders with a FolderPicker and recursively enumerate supported image files without copying them"
  - "Added folders and user-created tags must persist across launches using an AOT-compatible serialization approach"
  - "The main view must present a hierarchical folder browser on the left, a photo thumbnail browser in the center, and an editing or preview canvas on the right"
  - "Selecting a thumbnail must display that image in the preview canvas, where the user can zoom and pan"
  - "The app must support multi-selecting photos and adding or removing user-defined tags for the selected items"
  - "The photo browser must support grouping or filtering by at least two metadata dimensions such as folder, capture month, or image type"
  - "The editor must offer crop presets and a suggested crop region based on image content or a clearly identified deterministic fallback when content-aware analysis is unavailable"
  - "The editor must expose background removal and inpainting actions using an on-device Windows AI API when available, with explicit capability detection and a clear unavailable state rather than a cloud service"
test_notes: |
  The harness checks Native AOT itself (gate G3): it publishes Release with `winapp run --aot`,
  requires no IL2xxx/IL3xxx warnings (none suppressed), and confirms the published app shows a window.
  The app PID you are given is the harness's Debug launch; validate the requirements on it.
  Accessible names and keyboard navigation are checked automatically (Q0, Q1); judge Q5 states as usual.
  Create two temporary nested photo folders with a few PNG and JPEG files. Add both folders through the UI, verify recursive discovery, preview, zoom/pan, multi-select, tags, grouping/filtering, and persistence after restarting the app.
  Verify Windows AI actions show either working on-device behavior or an explicit unsupported/unavailable state. Any cloud call is a failure.
---

Create a production-quality WinUI 3 desktop app for organizing and editing photos stored in folders on the user's computer.

Users can add one or more folders. Keep references to those folders and enumerate their images recursively; do not copy the photos into application storage. Present a hierarchical folder browser on the left, a thumbnail browser in the center, and a larger editing canvas on the right. Selecting a photo opens it on the canvas with zoom and pan. Support multi-selection, user-defined tags, and grouping or filtering by useful metadata such as folder, capture month, and image type.

Provide non-destructive editing affordances for crop presets, a suggested crop region, background removal, and inpainting. Use on-device Windows AI capabilities when they are available. Detect capability at runtime and show a clear unavailable state when the device or API cannot support an AI action; do not use a cloud service as a fallback.

The application must be published with modern .NET Native AOT and must launch and work from the Native AOT Release publish output. Configure Native AOT before package restore, use dependencies and serialization/binding patterns that are compatible with trimming and AOT, resolve AOT and trim warnings rather than suppressing them, and test the actual published executable. A normal Debug build or non-AOT launch is not sufficient.
