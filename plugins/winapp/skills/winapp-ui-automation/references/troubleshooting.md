# UI automation troubleshooting

## Troubleshooting
| Error | Cause | Solution |
|---|---|---|
| "No running app found" | Wrong name or app not running | Try process name, window title, or PID |
| "Multiple windows match" | Several windows match `-a` | Use `-w <HWND>` from the listed options |
| "Selector matched N elements" | Text query matches multiple elements | Use a slug from the suggestions shown in the error, or from `inspect` output |
| "Element may have changed" | Slug hash doesn't match current element | Re-run `inspect` to get fresh slugs |
| "does not support any invoke pattern" | Element can't be invoked | The error shows the invokable ancestor slug if one exists — use that |
| "No UIA window found" | UIA can't see the window | Use `list-windows` to find HWND, then `-w` |
| Popup not in screenshot | Default capture path doesn't include unowned overlays | For one item in an open menu or flyout, pass its selector. To show popups in place or capture unowned overlays, follow the Screenshot section of this skill's `SKILL.md` to select a window with `-w <hwnd> --capture-screen` |
| `foreground_not_target` from `--capture-screen` | Windows refused the activation (focus-stealing prevention, UAC prompt, another window activating itself), so a screen capture would have recorded the wrong window | Click the target window, close the window that stole focus, then retry — or drop `--capture-screen` to capture the window directly |
| `element_not_found` during record | Selector given but element not in tree | Re-run `inspect` or `search` to get a fresh selector |
| `ambiguous_selector` during record | Plain-text selector matched multiple elements | Use a slug from the suggestions in the error message, or from `inspect` output |
| WGC unavailable during record | WGC capture init failed; no silent fallback | Check GPU/driver; use `--capture-screen` to explicitly request screen DC capture |
