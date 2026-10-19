---
name: markdown-editor-winui
description: "Tabbed markdown editor with live preview, file handling, and Windows integrations — tests TabView, menus, keyboard shortcuts, file I/O, and WebView2 rendering"
type: new
app_name: MarkdownEdit
requirements:
  - "The app must support multiple tabs, each containing an independent text editing area, with the ability to open new tabs (Ctrl+N) and close existing ones (Ctrl+W)"
  - "Each tab must show the file name (or 'Untitled') and indicate unsaved changes with a dot or modified marker in the tab header"
  - "File > Open (Ctrl+O) must let the user pick a .md or .txt file and open it in a new tab"
  - "File > Save (Ctrl+S) must save the current tab's content to disk, prompting with a Save File dialog if the file is new"
  - "File > Save As (Ctrl+Shift+S) must always prompt with a Save File dialog regardless of whether the file was previously saved"
  - "Closing a tab with unsaved changes must show a confirmation dialog offering Save, Don't Save, and Cancel"
  - "A split view shows the markdown source on the left and a live WebView2 preview on the right that updates as the user types, rendering headings, bold, italic, code blocks, links, images, and lists"
  - "A Find bar (Ctrl+F) must support searching within the current tab's text with match highlighting"
  - "A toolbar button or menu item must toggle between editor-only, preview-only, and split-pane modes"
  - "A status bar must show the current line number, column number, word count, and character count"
  - "The app must populate the taskbar JumpList with recently opened files using Windows.UI.StartScreen.JumpList"
  - "The editor must support undo (Ctrl+Z) and redo (Ctrl+Y) per tab"
  - "The app must include a Settings page with options for editor font size and preview theme (light/dark)"
---

I need a markdown editor for Windows — something clean and native-feeling.

The main thing is a split view: I want to write markdown on the left and see the rendered preview on the right, updating live as I type. Needs to handle all the standard markdown — headings, bold, italic, code blocks, links, images, lists. Use WebView2 for the preview.

Tab support is essential — I always have multiple files open. Standard file operations with the usual Windows shortcuts. If I close a tab without saving, warn me. I also want the ability to switch between editor-only, preview-only, and split modes.

Find (Ctrl+F) with highlighting, a status bar with line/column/word count, and undo/redo per tab. Recent files should appear in the taskbar JumpList so I can right-click and jump straight to a file. A Settings page for font size and preview theme would be nice.

Make it feel like it belongs on Windows — Fluent Design, proper menu bar, keyboard shortcuts, native look.
