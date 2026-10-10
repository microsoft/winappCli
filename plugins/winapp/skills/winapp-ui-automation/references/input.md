# Keyboard and hover input

### Hover (for tooltips, flyouts, hover states)
`--dwell-time <ms>` sets how long to wait after hovering (default: 800, range: 0–10000).
```powershell
winapp ui list-windows -a myapp # use the main window's HWND below, not the tooltip's

# Hover to trigger tooltip, then capture it (default 800ms dwell)
winapp ui hover btn-info-a1b2 -a myapp; winapp ui screenshot -w <hwnd> --capture-screen --output tooltip.png

# Longer dwell for apps with slow tooltip timers
winapp ui hover btn-info-a1b2 -a myapp --dwell-time 1200; winapp ui screenshot -w <hwnd> --capture-screen
```

### Send keyboard input
Synthesize keystrokes — the keyboard counterpart to `click`. Use for arrow/Tab/Enter navigation, shortcuts, and per-keystroke typing (vs `set-value`'s atomic write). Tokens are whitespace-separated: named keys (`enter`, `down`, `tab`, `esc`, `f5`), modifier combos (`ctrl+shift+t`), literal text (`hello`), and raw virtual keys (`vk=0xNN`).
```powershell
# Keyboard navigation then commit
winapp ui send-keys "down down enter" -a myapp

# Type the literal words "down down enter" instead of pressing those keys (text= escapes each token)
winapp ui send-keys "text=down text=down text=enter" -a myapp

# Same intent, less typing: --verbatim types the whole argument literally (and keeps exact whitespace)
winapp ui send-keys "down down enter" -a myapp --verbatim

# Shortcut: select all and delete
winapp ui send-keys "ctrl+a delete" -a myapp

# Focus a field, then type text into it
winapp ui send-keys "Hello world" --target txt-name-a1b2 -a myapp

# Transport: --via post-message (default, HWND-targeted, bypasses UIPI) or send-input (OS-wide)
winapp ui send-keys "enter" -a myapp --via send-input

# Fire a global hotkey: win+... is refused by default (acts on the shell); opt in with --allow-system-keys
winapp ui send-keys "win+shift+v" -a myapp --via send-input --allow-system-keys

# Screen-reader command: hold capslock or insert (Narrator+Ctrl+F12 toggles Narrator developer mode)
winapp ui send-keys "ctrl+capslock+f12" -a myapp --via send-input
```
- Screen-reader commands hold `capslock` or `insert` as a modifier (`ctrl+capslock+f12`) and require `--via send-input`; post-message errors because screen readers don't receive posted keys. If no screen reader is running, holding `capslock` turns Caps Lock on or off (the command warns); hold `insert` to avoid this.
- A token that starts with a modifier but has an unknown middle segment (`ctrl+a+b`) is an error, not literal text; use `text=` or `--verbatim` to type it.
- Default `post-message` is HWND-targeted and works across integrity levels, but can't fire `WH_KEYBOARD_LL` global hotkeys. It automatically retargets to the **focused child control** of the target window, so classic Win32/WinForms child-window controls (e.g. an edit box) receive the input. **WinUI 3 / UWP / XAML controls are windowless and ignore posted `WM_CHAR`/`WM_KEYDOWN`** — post-message can't deliver keys *or* text to them (the command emits a warning and still exits 0, since `PostMessage` can't confirm delivery). Use **`--via send-input`** for WinUI 3 / UWP / XAML apps.
- A token that collides with a key/modifier name (e.g. `enter`, `down`, `ctrl+a`) is pressed as that key. Prefix it with `text=` to type it as literal text instead — `text=enter` types the word "enter"; chain `text=` tokens to type a literal phrase like `text=down text=down text=enter`. Backslash escapes inside a `text=` value type whitespace the tokenizer would otherwise collapse: `\s`→space, `\t`→tab, `\n`→newline, `\r`→CR, `\\`→backslash (e.g. `text=a\s\sb` → "a  b"). When the *whole* argument is literal text, pass `--verbatim` instead of escaping each token: it types the entire keys argument as-is (no key/combo/`vk=`/`text=` parsing) and preserves exact whitespace — `send-keys "down down enter" --verbatim` types the words. (`--verbatim` does not decode backslash escapes; use a `text=` token for control characters.)
- `send-input` is fully real input but goes to the foreground window and is UIPI-blocked when injecting from elevated → AppContainer/AppX. It **rejects system-reserved combos** (`win+l`, `alt+f4`, `ctrl+shift+esc`, `ctrl+alt+del`, `alt+tab`, …) because those act on the OS/shell, not just the target — pass **`--allow-system-keys`** to opt in (e.g. to fire a global hotkey such as PowerToys' `win+shift+v` or `win+r`), or use `--via post-message` (window-scoped) to send one straight to the window. **`win+l` and `ctrl+alt+del` stay blocked even with `--allow-system-keys`** — `win+l` locks the workstation via `LockWorkStation()` (unrecoverable from automation), and `ctrl+alt+del` is a Secure Attention Sequence (SAS) that Windows drops from injected input regardless of the flag, so it errors (`invalid_arguments`) instead of falsely reporting success. On a locked/secure desktop `send-input` fails fast with `no_interactive_desktop`.
- Per-keystroke events: named keys/combos fire a real `KeyDown` on both transports. For literal typed text, `--via send-input` maps each char to its VK (+Shift) so each character fires a real `KeyDown` + OS-composed `WM_CHAR` (`TextChanged`) — use it when downstream logic keys off `KeyDown` (e.g. WinUI 3/WPF `TextBox`); bring the target window to the foreground first. `--via post-message` posts a `WM_CHAR` per character to the focused child control (raises `TextChanged`, lands correct text into classic Win32 controls across integrity levels) but does not fire a per-character `KeyDown`, and — because WinUI 3 / UWP / XAML controls are windowless — does **not** reach them at all (named keys and text alike); use `--via send-input` there.
- Long literal text on `--via send-input` is **auto-throttled**: the text is split into small character chunks injected one `SendInput` at a time with a brief pause between the chunks of that one run, so the target can drain its input queue and every character lands. A single unbroken burst overruns the queue and silently drops characters even though the command reports success. The pacing is scoped to a single long text run — short text, key names, and modifier combos (including sequences like `ctrl+a delete`) inject with no added delay, and the command emits an informational warning when a payload is large enough to be throttled. Because each paced chunk lands on whatever window is foreground, `send-input` re-verifies the target still owns the foreground before every continuation chunk and **aborts with `foreground_not_target`** if focus leaves the target mid-injection, so a focus change partway through can't spray the rest of the text into another window (a focus-changing chord such as `alt+tab` is exempt and is never treated as drift). For large bulk text, prefer `set-value` (atomic, no keystrokes or foreground needed) on controls that support it.
