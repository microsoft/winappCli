# Reading state, setting values, and file dialogs

### Read element state
```powershell
# Read text/value content (works for RichEditBox, TextBox, ComboBox, Slider, labels)
winapp ui get-value doc-texteditor-53ad -a notepad
winapp ui get-value SearchBox -a myapp
winapp ui get-value CmbTheme -a myapp              # reads ComboBox selected item via SelectionPattern

# Check toggle/selection state, value, scroll position
winapp ui get-property chk-agreecheckbox-b2c3 -a myapp --property ToggleState
winapp ui get-property txt-textbox-a4b1 -a myapp --property Value
winapp ui get-property cmb-modellist-d5e6 -a myapp --property IsSelected

# Read formatting across the whole text document (not the selection)
winapp ui get-property Document -a myapp --property FontWeight --json

# See what has keyboard focus
winapp ui get-focused -a myapp
```

For `get-focused`, `-a` includes windows in the selected process; `-w` restricts
focus to that exact window, excluding its owned popups. A control can belong to
the target through its parent window even when it omits its own process ID.
See `references/ui-json-envelope.md` for focus no-match and query-error handling.

To check a blank or cleared field, use `winapp ui wait-for SearchBox -a myapp --value ""`.
See [empty-value behavior](https://github.com/microsoft/winappcli/blob/main/docs/ui-automation.md#get-value)
for JSON output and reading the accessibility label separately.

`get-property` also accepts `FontName`, `FontSize`, `ForegroundColor`, `IsItalic`,
and `StrikethroughStyle`. Omit `--property` to include all six formatting attributes.
Treat `Mixed`, `NotSupported`, and `Unavailable` as distinct states, not formatting
values. Names are case-sensitive; unknown names fail with `invalid_arguments`.
See the [formatting reference](https://github.com/microsoft/winappcli/blob/main/docs/ui-automation.md#whole-document-text-formatting)
for units and state meanings, and `references/ui-json-envelope.md` for the JSON shape.

### Set values
To prepare a control for keyboard input, use `winapp ui focus <selector> -a <app>`
(keep `--on sandbox` when working in Sandbox). It activates the control's window
and verifies both foreground and keyboard focus before success. Do not use a
screenshot as a focus workaround. If activation is refused, inspect for a blocking
dialog and ask the user to activate the intended window; do not retry in a loop
or switch to the local desktop. See the [focus reference](https://github.com/microsoft/winappcli/blob/main/docs/ui-automation.md#focus)
for failure codes and recovery.

`set-value` writes programmatically (no keystrokes, no foreground) via a fallback chain: ValuePattern → RangeValuePattern (numeric) → LegacyIAccessible `put_accValue` for TextPattern-only edit controls.
```powershell
winapp ui set-value txt-searchbox-e5f6 "hello" -a myapp        # TextBox/ComboBox via ValuePattern
winapp ui set-value sld-volume-b2c3 75 -a myapp                # Slider via RangeValuePattern
winapp ui set-value doc-compose-9f3a "hello" -a myapp          # RichEdit/compose box via LegacyIAccessible
```
- The LegacyIAccessible fallback reaches rich-edit/compose controls that expose no ValuePattern, as long as their accessibility implements `put_accValue` (native Win32 rich-edit and Chromium/Electron/WebView2 compose boxes typically do).
- **WinUI 3 `RichEditBox` and WPF `RichTextBox` don't support programmatic value-setting** — by design they're read-only to UI Automation's value APIs (Text pattern, no settable Value pattern). `set-value` fails on them with a clear error — use `send-keys` (needs an unlocked, foregrounded desktop) to type into those instead. Note `get-value` can still *read* them via TextPattern.

### File dialog workaround
File open/save dialogs are standard Windows dialogs with UIA support. Interact with them using existing commands:
```powershell
# 1. Trigger the dialog (e.g., click "Open File" button)
winapp ui invoke btn-openfilebtn-a2b3 -a myapp

# 2. Find the dialog window
winapp ui list-windows -a myapp
# → Shows the main window + the dialog HWND
# Note: untitled zero-size windows are hidden by default; use --show-hidden to include them

# 3. Target the dialog, type the file path, and confirm
winapp ui set-value txt-1148-c4d5 "C:\path\to\file.png" -w <dialog-hwnd>
winapp ui invoke btn-open-e6f7 -w <dialog-hwnd>
```
Note: The filename input in standard file dialogs typically has AutomationId `1148`. Use `inspect -w <dialog-hwnd> --interactive` to discover the actual slugs.
