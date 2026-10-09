---
name: winapp-ui-automation
description: "Inspect, drive, and verify a running Windows app UI with winapp ui (UI Automation): find elements, click, type, read text and values, wait for state, take screenshots. Works with WinUI, WPF, WinForms, Win32, and Electron. Use to check, test, or act on a live app."
---

**If you can't run `winapp` yourself** (no shell, or the command is denied), give the user the exact `winapp` command(s) for their project instead of only describing the steps.

## When to use
- Inspecting a running Windows app's UI from the command line
- AI agents interacting with Windows applications (clicking buttons, reading text, taking screenshots)
- Verifying UI state during development or testing
- Automating UI workflows without Playwright or Selenium
- Debugging WinUI 3, WPF, WinForms, Win32, or Electron app UIs

## Prerequisites
- For UIA mode (any app): No setup needed — works with any running Windows app
- For input-injecting verbs (`click`, `hover`, `drag`, `touch`, `pen`, `scroll --wheel`, `send-keys --via send-input`): an **unlocked, interactive desktop** with the target window foregroundable. On a locked/secure desktop they fail fast with `no_interactive_desktop`. The UIA-pattern verbs (`inspect`, `search`, `get-*`, `wait-for`, `set-value`, `invoke`, `scroll --direction/--to`) are headless/locked-session friendly — prefer them in CI.
- `screenshot` is **not** in that group: it always takes an exclusive turn, so it queues behind other UI workflows, and capture can need a usable interactive desktop — the engine restores the target if it is minimized, and falls back to foregrounding it when frame capture is unavailable or `--capture-screen` is used.
- `--capture-screen` needs **exactly one window**. `-w <hwnd>` gives it one: that window's screen region, including anything visibly on top of it. If `-a` matches several top-level or owned windows the command fails with `invalid_arguments` before capturing; run `winapp ui list-windows -a <app>` and retry with `-w <hwnd>`.
- **If other UI workflows may run at the same time**, set one workflow id per logical workflow (see below). Nothing breaks without it, but your commands will not be recognized as belonging together.

## Coordinating with other UI workflows

Windows has one foreground window, one keyboard focus, one cursor, and one input stream. `winapp ui`
therefore makes desktop-driving commands take **cooperative turns** so concurrent workflows cannot
steal each other's focus or dismiss each other's menus. That is always on. Read-only commands never
wait.

Keeping the desktop *across* commands is opt-in — without an id, each command is a one-shot that
releases the desktop the moment it finishes:

```powershell
# Set once per logical UI workflow — same value for cooperating calls, different values for
# independent workflows (even from the same agent).
$env:WINAPP_UI_WORKFLOW_ID = [guid]::NewGuid().ToString()
```

Rules that matter when driving this from an agent:

- **Each tool call usually gets a fresh shell**, and there is no process-ancestry fallback, so
  commands are grouped ONLY by the id you inject. Without it every call is its own workflow.
- **A workflow with an id keeps its turn for four seconds** after its last command. That covers
  back-to-back commands in one script; it deliberately expires while you are reasoning. Treat it as
  a fallback for when you cannot say you are done — not as the way to finish.
- **Run `winapp ui yield` when you finish a known sequence.** It hands the desktop over immediately
  instead of making a waiting workflow sit out a four-second grace nobody needs. Yielding twice, or
  after the grace lapsed, is a harmless success.
- **After a reasoning gap, replay your setup.** Another workflow may have used the desktop, so
  reopen the menu / re-navigate, re-resolve the element, then act. Do not assume transient UI
  survived.
- **Prefer one tight script over many round trips** for a known sequence: `winapp ui invoke View -w
  $hwnd; winapp ui search "Status bar" -w $hwnd; winapp ui click "Status bar" -w $hwnd; winapp ui yield`.
- **`record` shares the turn with its own workflow**, so same-workflow clicks and typing are captured
  while it runs — but only if both commands carry the same id. A `record` with no id blocks everyone
  else for its whole duration.
- **Ordering is owner affinity, then FIFO.** An active workflow may keep issuing commands ahead of
  others already waiting; once it yields or its grace expires, waiters are served in arrival order.
- **Waiting is indefinite and cancellable.** After one second a notice on stderr says what holds the
  desktop and for how long (silent under `--json` and `--quiet`); Ctrl+C exits `130` with error code
  `cancelled` and the command never ran.
- **There is no hard cap** — a long script, unbounded recording, or failure loop can block other
  mutating workflows until it finishes or is stopped.

Commands that never wait: `status`, `list-windows`, `inspect`, `search`, `get-*`, `wait-for`.
Commands that wait for the turn but never take the desktop (headless/locked-session friendly):
`set-value`, `scroll-into-view`, `scroll --direction`/`--to`, `record`. They mutate the app, so they
queue behind another workflow. Inside your own workflow they overlap with other shared work — that
is how `record` captures the `set-value` calls it is recording — but they still wait behind an
earlier `DesktopExclusive` command of your own workflow, so a `click` followed by a `set-value` runs
in the order you wrote it.
Commands that take the desktop exclusively: `invoke`, `click`, `drag`, `hover`, `scroll --wheel`,
`touch`, `pen`, `focus`, `send-keys`, `screenshot`.

```powershell
# Finish a workflow deliberately rather than leaving the desktop reserved for four more seconds.
winapp ui yield
```

## Common patterns

### Read within a specific container

```powershell
winapp ui search "Welcome to MyApp" -a myapp --root MailRow --type Text --class-name TextBlock
winapp ui get-value Subject -a myapp --root MailRow --type TextBox
winapp ui wait-for Subject -a myapp --root MailRow --type Edit --value Ready --timeout 10000
```

Use `--root`, `--type`, and `--class-name` together or separately on `search`,
`get-property`, `get-value`, `wait-for`, and the commands that act on one
selected element (`invoke`, `set-value`, `click`, `focus`, and so on; not `send-keys --target`).
For `touch` and `pen`, filters need a selector; they can't be combined with `--at` or `--path`. The root must be unique; only its
descendants match. `wait-for` re-resolves it every poll, including when it is
initially absent. Type names and literal whole ClassName values ignore case.
The only type aliases are `TextBox` → `Edit` and `TextBlock` → `Text`.
See [Scoped and typed queries](https://github.com/microsoft/WinAppCli/blob/main/docs/ui-automation.md#scoped-and-typed-queries)
for the full type vocabulary, boundaries, and error behavior.

### Discover and interact
```powershell
# See what's clickable, then screenshot for context
winapp ui inspect -a myapp --interactive; winapp ui screenshot -a myapp

# Click and verify the page changed
winapp ui invoke btn-settings-a1b2 -a myapp; winapp ui wait-for pn-settingspage-c3d4 -a myapp --timeout 3000; winapp ui screenshot -a myapp

# Fill a form and submit
winapp ui set-value txt-searchbox-e5f6 "hello" -a myapp; winapp ui invoke btn-submit-7a90 -a myapp; winapp ui screenshot -a myapp
```

### Find visible text and click it
```powershell
# Search by text — output shows invokable ancestor
winapp ui search "Save changes" -a myapp
# Output:
#   lbl-savechanges-a1b2 "Save changes" (120,40 80x20)
#         ^ invoke via: btn-save-c3d4 "Save"

# Invoke by text — auto-walks to parent Button
winapp ui invoke 'Save changes' -a myapp
```

### Navigate multi-page apps
```powershell
# Click nav item, wait for page, inspect what's available
winapp ui invoke itm-samples-3f2c -a myapp; winapp ui wait-for pn-samplespage-b4e7 -a myapp; winapp ui inspect -a myapp --interactive
```

### Choose an exact action in tests
```powershell
winapp ui invoke SettingsCategory -a myapp --action select
winapp ui invoke AgreeCheckbox -a myapp --action toggle-on --json
winapp ui invoke Open -w <dialog-HWND> --type Button --action invoke
```

Use `--action` to avoid automatic pattern and ancestor fallback. Omit it for the
existing automatic behavior. With `--root`, `--type`, or `--class-name`, the
filters follow the same matching rules as read queries: exactly one element must
match inside the selected app/window, or the command fails without acting, and
the ancestor fallback is skipped. See the
[action reference](https://github.com/microsoft/winappCli/blob/main/docs/ui-automation.md#invoke)
for supported actions, scope, idempotent toggles, and failure recovery, and the
[JSON envelope](references/ui-json-envelope.md#ui-invoke---json) for action results.

### Disambiguate duplicate elements
```powershell
# When text search matches multiple elements, the error shows slugs for each — pick the right one
winapp ui invoke Submit -a myapp
# → Selector matched 3 elements:
#   [0] Button "Submit Order" → btn-submitorder-a1b2
#   [1] Button "Submit" → btn-submit-c3d4
# Use the slug: winapp ui invoke btn-submit-c3d4 -a myapp
```

## Key concepts
- **Selector brackets**: `inspect` and `search` output shows selectors in `[brackets]` — use the bracketed value with other `ui` commands. Selectors are either AutomationId (stable, developer-set) or generated slug (e.g., `btn-name-hash`).
- **AutomationId selectors**: When an element has a unique AutomationId, it becomes the selector directly (e.g., `[MinimizeButton]`). These survive layout changes and localization — preferred for stable targeting.
- **Slug selectors**: When no unique AutomationId exists, a generated slug is used (e.g., `[btn-close-a2b3]`). Format: `prefix-name-hash`. May go stale after UI changes.
- **Plain text search**: `search` and `invoke` accept plain text — `search Minimize` finds elements with "Minimize" in their Name or AutomationId (substring, case-insensitive). No special syntax needed.
- **`--interactive` flag**: Filters to elements you can invoke, click, or set-value (including text documents) with auto-depth 8 — the fastest way to see what you can act on
- **Invokable ancestor surfacing**: When a search result isn't invokable, the nearest invokable parent is shown with its selector
- **`;` chaining**: Chain commands with `;` to run multiple operations in one call, reducing agent round-trips
- **`-a` vs `-w`**: Use `-a` to find apps by name/title/PID. Use `-w <HWND>` for stable window targeting
- **Element markers**: `[on]`/`[off]` for toggles, `[collapsed]`/`[expanded]`, `[scroll:v]`/`[scroll:h]`/`[scroll:vh]` for scrollable containers, `[offscreen]`, `[disabled]`, `value="..."` for editable elements

## Usage

### Connect and discover
```powershell
# Core loop and examples: `winapp ui --help`. Connect and see interactive elements in one call:
winapp ui status -a myapp; winapp ui inspect -a myapp --interactive
```

### Inspect element tree
```powershell
winapp ui inspect -a myapp --interactive      # elements you can invoke, click, or set-value; auto-depth 8
winapp ui inspect -a myapp --depth 5          # deeper tree at depth 5
winapp ui inspect txt-searchbox-e5f6 -a myapp  # subtree rooted at element
winapp ui inspect btn-settings-a1b2 -a myapp --ancestors  # walk up from element to root
winapp ui inspect -a myapp --hide-offscreen   # hide offscreen elements
```

### Find elements
```powershell
winapp ui search Close -a myapp               # finds elements with "Close" in name or automationId
winapp ui search Button -a myapp              # finds elements with "Button" in name (also matches type names)
winapp ui search image -a myapp               # case-insensitive substring match
```

### Screenshot
```powershell
# App screenshot (may include owned windows in a labeled composite)
winapp ui screenshot -a myapp --output page.png

# Crop to element; capture with popups visible
winapp ui screenshot txt-searchbox-e5f6 -a myapp --output search.png
winapp ui list-windows -a myapp # use the main window's HWND below
winapp ui screenshot -w <hwnd> --capture-screen --output with-popups.png

# Bring window to foreground first (matches what the user is currently seeing)
winapp ui screenshot -a myapp --focus --output focused.png
```

Default capture includes owned windows even with an explicit main HWND; it produces one labeled composite, not separate image files. An element selector for an item in an open menu, flyout, tooltip, or teaching tip crops from that item's popup window, so you don't need `--capture-screen` for it. For scope and on-screen overlay placement, see [Screenshot](https://github.com/microsoft/WinAppCli/blob/main/docs/ui-automation.md#screenshot). With `--on sandbox`, the reported screenshot path is the delivered host destination.

### Wait for UI state
```powershell
winapp ui wait-for btn-submit-a1b2 -a myapp --timeout 5000
winapp ui wait-for itm-status-c3d4 -a myapp --value "Complete" --timeout 5000
```

## Tips
- Use `--interactive` with `inspect` as your first command — it shows only what you can act on
- Chain commands with `;` to reduce round-trips (see note below on why not `&&`)
- Use slugs from output to target specific elements — they're hash-validated and shell-safe
- Use plain text search to find elements: `search Minimize`, `invoke Submit`
- When multiple elements match text search, the error shows slugs for each — pick the right one
- Use `get-property --property ToggleState` to verify checkbox/toggle state after invoke
- `scroll` auto-finds the nearest scrollable parent
- To capture one item in an open menu, flyout, tooltip, or teaching tip, pass its selector to `screenshot` or `record`; it is captured from its popup window
- Follow [Screenshot](#screenshot) to select a window with `-w <hwnd> --capture-screen` when you need popups in place over the window, or overlays the app doesn't own
- Follow the Hover section in `references/input.md` to capture tooltips and hover-triggered UI in place
- Use `--focus` to foreground the target window before capture without switching to screen-DC capture (default capture path uses Windows.Graphics.Capture and works while occluded)
- Use `--hide-disabled` and `--hide-offscreen` to reduce noise
- If `focus` or another foreground action is refused, look for a blocking dialog and ask the user to activate the intended window; don't retry in a loop or switch from Sandbox to the local desktop
- `win+…` shortcuts act on the Windows shell and are refused unless you pass `--allow-system-keys`; use it only for a shortcut the user asked for

### Why `;` instead of `&&`
Use `;` (not `&&`) to chain commands. PowerShell's `&&` operator can freeze when a native CLI writes to stderr or uses ANSI escape sequences — this causes a pipeline deadlock. `;` runs each command unconditionally and avoids this issue. This is also better for agent workflows: you usually want the screenshot to run even if the invoke had a non-zero exit (to see what went wrong).

## Automating in Windows Sandbox

```powershell
winapp run . --on sandbox --detach
winapp ui inspect --on sandbox -a MyApp
winapp ui screenshot --on sandbox -a MyApp -o .\result.png
```

Use `--detach` before follow-up UI commands, and retain `--on sandbox` with guest PIDs
or window handles. Inject the same `WINAPP_UI_WORKFLOW_ID` into cooperating guest calls;
finish with `winapp ui yield --on sandbox` after all of them complete.

Use `winapp-sandbox` for setup consent, connected-client requirements, brief focus
changes during setup/reconnect, and capture recovery. App screenshots and recordings,
including default filenames and `--frames` directories, return to the host.

## Load when

| Read | When |
|---|---|
| `references/input.md` | Sending keys, shortcuts, or text (including WinUI `--via send-input`), or hovering for tooltips |
| `references/gestures.md` | Drag-and-drop, sliders, touch gestures, pen/ink, or scrolling a container |
| `references/capture.md` | Recording an H.264 video of a workflow |
| `references/dialogs-and-state.md` | Reading properties/toggle/selection state, setting values, or handling a file open/save dialog |
| `references/ui-json-envelope.md` | Parsing `--json` output or error envelopes in a script |
| `references/troubleshooting.md` | A ui command fails, finds nothing, or targets the wrong window |

## Related skills

- `winapp-sandbox` — run and automate the app in Windows Sandbox instead of the user's desktop
- `winapp-find-api` — check an API before writing code against it

Run `winapp <command> --help` for current command options, or `winapp --cli-schema` for the complete machine-readable command schema.
