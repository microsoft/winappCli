---
name: media-gallery
description: "Build a WinUI 3 media gallery app with advanced controls and platform features"
type: new
app_name: MediaGallery
requirements:
  - "Core: a top-mode NavigationView switches between Gallery, Favorites, and Settings; dropping image files from File Explorer imports them; a CommandBar has Import, Delete, Select All, and Share"
  - "The Gallery page shows image thumbnails in a GridView or ItemsRepeater with a uniform grid layout"
  - "Clicking an image opens a large preview in a ContentDialog or overlay"
  - "Share uses the Windows Share UI to share the selected images"
  - "The Favorites page shows only images marked as favorite, using the same grid layout"
  - "Right-clicking an image shows a menu with Open, Add to Favorites, Copy, and Delete, and they work"
  - "The Settings page has a theme ToggleSwitch, a thumbnail size ComboBox (Small/Medium/Large), and a NumberBox for the slideshow interval"
  - "An InfoBar reports how many images were imported, and a ProgressRing is visible while images load or import"
  - "Image metadata (filename, dimensions, file size, date) is shown in a TeachingTip or Flyout for an image"
---

Build me a media gallery app for browsing, organizing, and sharing photos.

Requirements:
- WinUI 3 desktop app with Fluent Design (Mica backdrop, custom title bar)
- NavigationView in Top mode with 3 pages: Gallery, Favorites, Settings
- Gallery page:
  - GridView or ItemsRepeater with UniformGridLayout for image thumbnails
  - Drag-and-drop support: drop images from File Explorer to import
  - Click image to see full-size preview in a ContentDialog or overlay
  - ProgressRing while loading images
- CommandBar with Import, Delete, Select All, Share buttons
- Share button uses Windows Share contract (IDataTransferManagerInterop COM interop for desktop)
- Favorites page: shows images the user marked as favorite, same grid layout
- Right-click context menu (MenuFlyout): Open, Add to Favorites, Copy, Delete
- Settings page:
  - ToggleSwitch for dark/light theme
  - ComboBox for thumbnail size (Small/Medium/Large)
  - NumberBox for slideshow interval
- InfoBar notification when import completes (e.g. "Imported 12 images")
- Image metadata display (filename, dimensions, size, date) in TeachingTip or Flyout
- Make sure the app builds and runs
