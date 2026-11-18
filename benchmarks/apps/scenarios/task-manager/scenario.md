---
name: task-manager
description: "app that tests deep OS integration, custom windowing (AppWindow), Win32 interop for system tray, and real-time chart rendering."
type: new
app_name: MiniResourceMonitor
requirements:
  - "Core: a toggle switches the window into a compact overlay (always-on-top) mode, and the app can minimize to the system tray"
  - "The tray icon restores the main window (double-click or its menu)"
  - "CPU and memory values are plausible and track the real system load"
  - "Live line charts for CPU, memory, disk read/write, and network send/receive update at least once per second on a scrolling time axis"
  - "Each chart shows its current value as a number (e.g. 'CPU 34%', 'Memory 8.2 / 16.0 GB')"
  - "The charts keep at least the last 60 seconds of history visible"
---

Build me something like a mini Windows Task Manager. I want to monitor my system resources — CPU, memory, disk, and network — in real time.

The main thing I care about is a Performance tab with live-updating charts, like the ones in the real Task Manager. Line charts that scroll over time showing CPU percentage, memory usage, disk read/write, and network send/receive. Each chart should show the current value as a number too, not just the line.

It should also support a compact overlay mode so I can keep it always-on-top in a corner, and minimize to the system tray so it's out of the way but still running. Make sure the app looks and feels modern and belongs on Windows.
