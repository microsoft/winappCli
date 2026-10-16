---
name: file-explorer-shell
description: "Build a WinUI 3 file explorer with Windows shell integrations"
type: new
app_name: FileExplorer
requirements:
  - "Core: multiple tabs (open new, close existing), each showing a folder; the file list shows name, size, type, and date modified columns; clicking a column header sorts ascending/descending"
  - "Back, Forward, and Up buttons navigate the folder history"
  - "A TreeView or navigation pane provides folder navigation with expand/collapse"
  - "A BreadcrumbBar or address bar shows the current path and accepts a typed path"
  - "Double-clicking a file opens it with the system default handler; double-clicking a folder navigates into it"
  - "The right-click menu on files/folders offers Open, Copy, Cut, Paste, Rename, Delete, and Properties, and they work"
  - "A search or filter box narrows the file list by name as the user types"
  - "Shell integration: sharing selected files uses the Windows Share UI, and recently opened folders appear in the taskbar JumpList"
  - "A status bar shows the number of items in the current folder and the number of selected items"
---

Build me a file explorer with shell integrations like sharing, recent files in taskbar, and more.

Requirements:
- WinUI 3 desktop app with modern Fluent Design (Mica backdrop, custom title bar)
- Tabbed interface: multiple tabs, each showing a different folder, with new tab and close tab support
- Back, Forward, and Up navigation buttons for folder history
- TreeView-based folder navigation in a left sidebar with expand/collapse
- BreadcrumbBar or address bar showing the current path, with ability to type a path directly
- File list view with columns: name, size, type, date modified
- Click column headers to sort ascending/descending
- Double-click a file to open it with the system default handler
- Double-click a folder to navigate into it
- Right-click context menu on files/folders: Open, Copy, Cut, Paste, Rename, Delete, Properties
- Search/filter box that filters the file list by name as you type
- Share button that uses the Windows Share contract to share selected files
- JumpList integration: recently opened folders appear in the taskbar right-click menu
- Status bar showing item count and selection count
- Make sure the app builds and runs
