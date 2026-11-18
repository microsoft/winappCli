---
name: photo-organizer
description: "WinUI 3 photo organizer that naturally requires multiple CommunityToolkit controls (masonry layout, chip-style tag input, image crop, resizable split, sort/filter)"
type: new
app_name: PhotoOrganizer
requirements:
  - "Core: photos use a masonry/staggered layout; the collection sorts by name, date taken, and file size and filters by tag without rebuilding the underlying list; each photo can get a color tag from a picker with both a spectrum and preset swatches; Frame-based navigation switches between Library and Settings"
  - "Two-pane layout: the photo collection on the left and the selected photo's editor on the right, with a vertical splitter the user can drag to resize the panes"
  - "Tag input uses removable chip tokens: Enter or comma commits a tag, Backspace removes the last chip, and clicking a chip's X removes it"
  - "The editor crops the selected photo with rectangle and circle shapes, and Save crop writes the cropped image"
  - "The Settings page has at least 4 Windows 11-style settings rows (header, description, icon, control) plus an expandable 'Import behavior' group with its sub-settings"
---

Build me a photo organizer app for browsing, tagging, and cropping personal photos.

Layout & navigation:
- WinUI 3 desktop app with Fluent Design (Mica backdrop, custom title bar)
- NavigationView (Left pane) with two pages: **Library** and **Settings**
- Frame-based page navigation

Library page (the main UX):
- Two-pane layout split vertically:
  - **Left pane** (collection): photos arranged in a **masonry / staggered layout** — different aspect ratios pack tightly without forcing uniform row heights
  - **Right pane** (editor): selected photo with cropping tools
  - The user must be able to **drag a vertical splitter** between the two panes to resize them
- Above the left pane:
  - A **chip-style tag input** — user types a tag, presses Enter or comma to commit it as a removable token; Backspace removes the last token; clicking the X on a chip removes it
  - Sort dropdown: Name | Date taken | File size
  - Filter dropdown: pick one of the existing tags to filter
  - Sorting and filtering must NOT rebuild the underlying photo list (use a view layer that wraps the collection)
- Right pane editor:
  - **Image crop tool**: user drags a crop region; supports both **rectangle and circular** crop shapes (toggle)
  - "Save crop" button writes the cropped image back
  - **Color tag picker**: full color picker with a spectrum AND a preset palette of swatches (the user can quickly pick from common colors OR pick a custom one). The chosen color is stored as a "color tag" on the photo.

Settings page (Windows 11 style):
- At least 4 individual settings rows, each with: header text, description text, an icon, and an interactive control (ToggleSwitch / ComboBox / etc.):
  - "Theme" — ComboBox: System / Light / Dark
  - "Show file metadata in tooltip" — ToggleSwitch
  - "Default crop shape" — ComboBox: Rectangle / Circle
  - "Library folder" — clickable row that opens a folder picker
- One **expandable group** that contains related sub-settings:
  - "Import behavior" expander, expands to reveal:
    - ToggleSwitch: "Auto-import from Pictures folder"
    - ComboBox: "When duplicate found" (Skip / Replace / Keep both)
    - NumberBox: "Maximum file size (MB)"

Quality bar:
- Make sure the app builds and runs
- All interactive controls have AutomationProperties.AutomationId
- Mica backdrop and custom TitleBar
