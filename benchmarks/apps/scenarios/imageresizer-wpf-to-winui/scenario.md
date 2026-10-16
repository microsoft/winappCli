---
name: imageresizer-wpf-to-winui
description: 'Convert ImageResizer PowerToy from WPF to WinUI 3'
type: convert
app_name: ImageResizer
original_app:
  source_dir: '{repo_root}\..\PowerToys\src\modules\imageresizer'
test_assets:
  - name: 'test image'
    path: '{scenario_dir}\testimage.png'
    description: 'test image to use for verifying the resize functionality.'
requirements:
  - "The size selector ComboBox must contain preset sizes (Small, Medium, Large, Phone, Custom)"
  - "All 4 checkboxes must be present and toggleable: shrink only, ignore orientation, replace originals, remove metadata"
  - "The Settings button must open the PowerToys settings page or show a settings panel"
  - "Click Resize with a test image loaded. The resize must complete within 15 seconds and produce an output file. Verify the output file dimensions differ from the original"
  - "The Cancel button must close the app"
  - "All control icons should use WinUI SymbolIcon or FontIcon (not legacy WPF icons)"
---

Convert the ImageResizer PowerToy from WPF to WinUI 3.

Create a new WinUI 3 project that replaces the WPF UI while keeping the same functionality.

Requirements:
- Create a new WinUI 3 project
- Convert all views, windows, popups, panels, etc
- Keep the same functionality, if not possible to keep the same functionality with winui3, tell me why
- Make sure the app builds and runs
- The resize operation must work end-to-end: load image, click resize, produce output file
- Ensure file explorer integrations works also, allowing a user to right click a file in file exploerer to resize
