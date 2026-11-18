---
name: text-editor-winui
description: "Build a WinUI 3 tabbed text editor with JumpList integration, find/replace, and standard editing features"
type: new
app_name: QuickEdit
requirements:
  - "The app must support multiple tabs, each containing an independent text editing area, with the ability to open new tabs and close existing ones"
  - "Each tab must show the file name (or 'Untitled') and indicate unsaved changes with a dot or asterisk in the tab header"
  - "File > Open must let the user pick a text file and open it in a new tab"
  - "File > Save must save the current tab's content to disk, prompting with a Save File dialog if the file is new"
  - "File > Save As must always prompt with a Save File dialog regardless of whether the file was previously saved"
  - "Closing a tab with unsaved changes must show a confirmation dialog offering Save, Don't Save, and Cancel"
  - "The app must populate the taskbar JumpList with recently opened files using Windows.UI.StartScreen.JumpList"
  - "Clicking a file in the JumpList must launch the app (or activate it) and open that file in a new tab"
  - "A Find and Replace bar (Ctrl+H) must support searching within the current tab's text with match highlighting and replace/replace all"
  - "Ctrl+F must open Find, Ctrl+H must open Find and Replace, Ctrl+S must Save, Ctrl+Shift+S must Save As, Ctrl+N must open a new tab, Ctrl+W must close the current tab"
  - "A status bar must show the current line number, column number, and total character count"
  - "The text editor must support undo (Ctrl+Z) and redo (Ctrl+Y) per tab"
---

I need a simple but solid text editor — something like Notepad but with tabs. I open a ton of files at once and Notepad only does one at a time.

Each tab should show the file name and whether there are unsaved changes. Standard file operations — open, save, save as — and if I try to close a tab without saving it should warn me. Keyboard shortcuts for everything: Ctrl+S, Ctrl+N, Ctrl+W, the usual.

I want recent files to show up in the taskbar JumpList so I can right-click the icon and jump straight to something I had open before.

Also need find and replace (Ctrl+H) with highlighting, and a status bar at the bottom showing line/column numbers. Undo and redo per tab obviously. Nothing fancy, just a clean, fast text editor that feels native on Windows.
