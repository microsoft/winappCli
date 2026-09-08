<!-- mslearn: true -->
<!-- ms.topic: reference -->
<!-- description: Inspect and interact with running Windows application UIs from the command line using winapp CLI UI automation commands. -->
# UI Automation

Inspect and interact with running Windows applications from the command line.
Used by AI agents and developers for UI testing, debugging, and automation.

## Overview

`winapp ui` provides commands for inspecting and interacting with Windows app UIs.
Uses Windows UI Automation (UIA). Works with any Windows app — WPF, WinForms, Win32, Electron, and WinUI 3.
Most commands drive the app through UIA patterns (no input injection). The exceptions inject real input: `ui click`/`ui hover`/`ui drag` use mouse simulation, `ui touch`/`ui pen` synthesize touch and pen/stylus input, and `ui send-keys` synthesizes keyboard input — for controls and scenarios that UIA patterns can't drive.

> [!IMPORTANT]
> **Interactive-desktop requirement (input-injecting verbs).** `click`, `hover`, `drag`, `touch`, `pen`, `scroll --wheel`, and `send-keys --via send-input` synthesize OS-level input, so they need an **unlocked, interactive desktop** with the target window in the foreground. On a **locked workstation or secure desktop** (LogonUI/UAC) they can't inject and fail fast with **`no_interactive_desktop`** (distinct from the elevation/`foreground_not_target` cases). `touch`/`pen` additionally refuse when no window resolves (**`no_target`**); a coordinate outside the target window is a **non-fatal warning** (a `warnings[]` entry under `--json`, or a warning line in text mode) and injection still proceeds — consistent with the mouse verbs. Everything else — `inspect`, `search`, `get-property`, `get-value`, `wait-for`, `set-value`, `invoke`, `scroll --direction/--to`, `screenshot` — drives the app through UIA patterns and is **headless/locked-session friendly**. Prefer the UIA-pattern verbs in CI; reserve the injection verbs for scenarios that genuinely need real input. Before injecting, the gesture verbs also **re-resolve the target element** and refuse with **`target_moved`** if it's still animating/relocating, rather than landing input on empty space.

## Quick Start

```bash
# Connect to any app and see its UI tree
winapp ui inspect -a notepad

# Find specific elements
winapp ui search Button -a notepad

# Activate an element
winapp ui invoke Close -a notepad

# Take a screenshot
winapp ui screenshot -a notepad
```

## Targeting Apps

### By process name
```bash
winapp ui inspect -a notepad
winapp ui inspect -a slack            # auto-picks visible window for multi-process apps
winapp ui inspect -a imageresizer     # partial match: finds PowerToys.ImageResizer
```

### By window title
```bash
winapp ui inspect -a "LICENSE - Notepad"
winapp ui inspect -a "Fix WinApp"     # partial title match
```

### By PID
```bash
winapp ui inspect -a 12345
```

### By HWND (stable — survives tab/title changes)
```bash
# Discover HWNDs
winapp ui list-windows -a Terminal
  → HWND 985238: "🤖 Testing" (WindowsTerminal, PID 21228)
  → HWND 131906: "Fix WinApp" (WindowsTerminal, PID 21228)

# Target specific window
winapp ui inspect -w 131906
winapp ui screenshot -w 131906
```

Use `-a` for discovery, `-w` for stable targeting. When `-a` matches multiple windows, the command lists them with HWNDs for you to pick.

## Selectors

Target elements using the selector shown in `[brackets]` in inspect/search output.
There are three types of selectors:

| Selector | Meaning | Example |
|---|---|---|
| `MinimizeButton` | AutomationId (shown when unique — stable, preferred) | `winapp ui invoke MinimizeButton -a myapp` |
| `btn-close-d1a0` | Semantic slug (shown when no unique AutomationId) | `winapp ui invoke btn-close-d1a0 -a myapp` |
| `Submit` | Plain-text search against Name/AutomationId (case-insensitive substring) | `winapp ui invoke Submit -a myapp` |

**AutomationId selectors** are developer-set identifiers (`AutomationProperties.AutomationId` in XAML).
When an AutomationId is unique across the entire UI tree, `inspect` and `search` show it directly
as the selector — these survive layout changes, localization, and tree restructuring.

**Slug selectors** (e.g., `btn-close-d1a0`) are generated when no unique AutomationId exists.
Format: `prefix-name-hash`. The hash validates element identity but may go stale after UI changes.

### Inspect output format

The `inspect` command shows the element tree with colored output (selector in cyan, name in green, metadata in gray):
```
TabView Tab (0,-1 1200x48)
  TabListView List (4,-1 1100x48)
    tab-newtab-5f5b TabItem "New Tab" (14,-1 200x48)
  NewTabButton SplitButton "New Tab" [collapsed] (1104,5 96x36)
Found 10 elements (--depth 3). Use the first token as selector, e.g.: winapp ui invoke TabView -a terminal
```

The **first word** on each line is the selector — use it with other `ui` commands.
When an element has a unique AutomationId, it's used directly (e.g., `TabView`, `NewTabButton`).
When no unique AutomationId exists, a generated slug is used (e.g., `tab-newtab-5f5b`).

### Semantic slugs

Slugs use the format: `prefix-normalizedname-hash` where:
- **prefix** — 3-letter type abbreviation (btn, txt, chk, cmb, itm, tab, img, lbl, pn, win, grp, lnk, mnu, etc.)
- **normalizedname** — lowercase alphanumeric from AutomationId (preferred) or Name, max 15 chars
- **hash** — 4-char hex hash of the element's RuntimeId (validates element identity)

Slugs are shell-safe (no special characters), unique, and can be used directly as arguments. The hash provides staleness detection — if the element has been replaced, you get: "Element may have changed. Re-run inspect."

Elements with no name or AutomationId show only prefix + hash (e.g., `pn-c8a3`).

### Disambiguating multiple matches

Slugs from `inspect`/`search` output are unique, but can change across layout changes - use them over plain type names or text when multiple matches. When a selector is ambiguous, the CLI prints all matches with their slugs so you can pick the right one and re-run with that slug.

```bash
winapp ui search Button -a myapp            # shows: btn-ok-a1b2 "OK", btn-cancel-c3d4 "Cancel"
winapp ui invoke btn-ok-a1b2 -a myapp       # invoke using slug (preferred)
winapp ui invoke btn-cancel-c3d4 -a myapp   # invoke the other Button by its slug
```

### Plain text search
Use plain text to search for elements — no special syntax needed:
```bash
winapp ui search Minimize -a notepad        # finds elements with "Minimize" in Name or AutomationId
winapp ui search Close -a notepad           # case-insensitive substring match
winapp ui invoke Minimize -a notepad        # search + invoke in one step (disambiguates if needed)
winapp ui search "Save" -a notepad          # find elements containing "Save"
winapp ui search "error" -a myapp           # case-insensitive match
```

When a text search matches multiple elements (e.g., SettingsExpander where Group, Button, and Text all share the same name), the CLI automatically picks the only invokable element. If multiple are invokable, it lists all matches with slugs.

For non-invokable search results (e.g., a TextBlock inside a Button), the search
automatically surfaces the nearest **invokable ancestor** — the parent element you can use with `invoke`.
This works for all search selectors:

```
  lbl-savechanges-a1b2 "Save changes" (120,40 80x20)
        ^ invoke via: btn-save-c3d4 "Save"
```

The surfaced selector can be used directly:
```bash
winapp ui invoke btn-save-c3d4 -a myapp    # invoke the parent Button
```

## Commands

### status
Connect to an app and show connection info.
```bash
winapp ui status -a notepad
winapp ui status -a notepad --json
```

### inspect
View the UI element tree. Output shows semantic slugs with 2-space indentation for hierarchy:
```bash
winapp ui inspect -a notepad                    # full window tree, depth 3
winapp ui inspect -a notepad --depth 5          # deeper tree
winapp ui inspect txt-searchbox-e5f6 -a notepad # subtree rooted at element
winapp ui inspect --ancestors btn-close-d1a2 -a notepad  # walk up from element to root
winapp ui inspect -a myapp --interactive        # invokable elements only, auto-depth 8
winapp ui inspect -a myapp --hide-disabled      # hide disabled elements
winapp ui inspect -a myapp --hide-offscreen     # hide offscreen elements
```

Example output (default):
```
win-aidevgalleryp-f1a3 "AI Dev Gallery Preview" (94,206 1280x1023)
  pn-c8a3 (102,207 1264x1014)
    btn-minimize-d1a0 "Minimize" (1222,206 48x48)
    btn-maximize-e2b1 "Maximize" (1270,206 48x48)
    itm-samples-3f2c "Samples" (102,330 72x62)
```

Example output (`--interactive` — invokable elements only, flat list):
```
btn-minimize-d1a0 "Minimize" (1222,206 48x48)
btn-maximize-e2b1 "Maximize" (1270,206 48x48)
btn-close-d1a2 "Close" (1318,206 48x48)
itm-home-7b3e "Home" (102,268 72x62)
itm-samples-3f2c "Samples" (102,330 72x62)
itm-models-9a4f "Models" (102,392 72x62)
```

Elements may show these state markers:
- `[on]` / `[off]` / `[indeterminate]` — toggle/checkbox state
- `[collapsed]` / `[expanded]` — expand/collapse state for trees, combo boxes, menu items
- `[scroll:v]` / `[scroll:h]` / `[scroll:vh]` — scrollable container (vertical, horizontal, or both)
- `[offscreen]` — element is not visible on screen
- `[disabled]` — element is not enabled
- `value="..."` — current text content for editable elements (when different from Name)

### search
Find elements matching a selector. Output shows semantic slugs:
```bash
winapp ui search Button -a notepad              # all buttons
winapp ui search Close -a notepad               # finds elements with "Close" in name
winapp ui search SearchBox -a notepad           # finds elements with "SearchBox" in name or AutomationId
winapp ui search Button --max 10 -a notepad     # limit results
```

Example output:
```
  btn-minimize-d1a0 "Minimize" (1222,206 48x48)
  btn-maximize-e2b1 "Maximize" (1270,206 48x48)
  btn-close-d1a2 "Close" (1318,206 48x48)
```

Slugs shown in output (e.g., `btn-minimize-d1a0`) can be used directly with other commands:
```bash
winapp ui invoke btn-minimize-d1a0 -a notepad
```

### get-property
Read property values from an element. Includes pattern-specific state (ToggleState, Value, IsSelected, etc.).
```bash
winapp ui get-property btn-submit-7a90 -a myapp              # all properties
winapp ui get-property chk-checkbox-b2c3 -p ToggleState -a myapp   # checkbox state
winapp ui get-property txt-textbox-a4b1 -p Value -a myapp          # current text value
winapp ui get-property cmb-combobox-d5e6 -p ExpandCollapseState -a myapp  # expanded or collapsed
```

### screenshot
Capture a window or element as PNG. When multiple windows exist (e.g., app + open dialog), they are composited into a single PNG with each window stitched in.
```bash
winapp ui screenshot -a notepad                     # saves screenshot.png in cwd
winapp ui screenshot -a notepad --output my.png     # custom filename
winapp ui screenshot -a notepad --json              # returns file path as JSON
winapp ui screenshot -w 131906                      # target specific HWND (+ its dialogs)
winapp ui screenshot txt-searchbox-e5f6 -a myapp          # crop to element bounds
winapp ui screenshot -a myapp --capture-screen      # capture from screen (includes popups/overlays; foregrounds window)
winapp ui screenshot -a myapp --focus               # bring window to foreground first, then capture (default WGC path)
```

When dialogs or popups are open, all windows are composited into one PNG so you can see the full UI state in a single image.

The default capture path uses **Windows.Graphics.Capture (WGC)**, reading the actual DWM-composited surface — preserving rounded corners, transparency, and working even while the window is occluded by other windows. If WGC is unavailable (older Windows builds) the CLI falls back to **PrintWindow**.

Use `--capture-screen` when you need to capture popup menus, dropdowns, flyouts, or tooltip overlays that aren't owned by the target window. `--capture-screen` reads from the screen DC and brings the window to the foreground first. Use `--focus` if you just want to foreground the window without switching capture modes (e.g., to ensure the screenshot matches what the user is currently looking at).

### record
Record a window or element region to an H.264 MP4. By default, recording continues until Ctrl+C or, for redirected stdin, a newline or EOF.

```bash
# Record for 10 seconds
winapp ui record -a myapp --duration-sec 10 --fps 15 --output demo.mp4

# Add agent-readable frames
winapp ui record -a myapp --frames --duration-sec 10 --fps 10 --output demo.mp4 --json

# Stop an unbounded recording through stdin
"" | winapp ui record -a myapp --json --output capture.mp4

# Include screen overlays and popups
winapp ui record -a myapp --capture-screen --duration-sec 5 --output with-popups.mp4
```

**Options:**
- `--duration-sec N` — Record for N seconds. Default 0 records until stopped.
- `--fps N` — Target frames per second (default 15).
- `--max-edge N` — Downscale so the longest edge is at most N pixels (0 = no downscale).
- `--capture-screen` — Capture from the screen DC (includes overlays/popups; foregrounds the window).
- `--output <path>` — Output MP4 path. Defaults to `recording-<timestamp>-<guid>.mp4`.
- `--frames` — Write timestamped JPEG evidence to `<output-name>.frames`. Supports 1-30 fps and `--max-edge` 64-4096 (default 1280). Frame data is capped at 1 GiB; the MP4 continues if the cap is reached.

**Agent-readable frame artifacts:**

```text
demo.mp4
demo.frames/
  manifest.json
  frames.ndjson
  frames/
    frame-000000-t000000000012.jpg
```

`frames.ndjson` has one line per sample with `sampleIndex`, monotonic `elapsedMs`, MP4-relative `mediaTimeMs`, `imageIndex`, `file`, and `changed`. Consecutive pixel-identical samples reuse the preceding quality-85 JPEG.

`manifest.json` records the request, timing, MP4 status, image dimensions, and status (`complete`, `partial`, or `truncated`). Truncated timing covers the retained prefix, while `video` describes the complete MP4.

With `--frames`, existing MP4 and frame paths are not replaced. If MP4 finalization fails, preserved frames are published under `<output-name>.frames.partial-*`. Frame artifacts contain unencrypted screen content; handle them like screenshots or video.

**Capture modes** (reported in the JSON `mode` field):
- `wgc` — Windows Graphics Capture (default; works while the window is occluded).
- `printwindow` — GDI PrintWindow (fallback when WGC is unavailable on this system/session; re-run with `--capture-screen` to use screen DC instead).
- `screen` — Screen DC via `--capture-screen` (includes overlays/popups; brings the window to the foreground).

**JSON output (`--json`):**
- **stdout:** Final recording result, including cadence, stop reason, optional `frameArtifacts`, and warnings.
- **stderr:** One JSON object per line: a `recording-started` event after the first frame, followed by an error if recording later fails. Frame paths are included only when frame output is active.

**Error codes:**
- `element_not_found` — The selector did not match.
- `ambiguous_selector` — The selector matched multiple elements; use a suggested slug.
- `invalid_arguments` — An option value is invalid.
- `output_exists` — With `--frames`, the MP4 or frame directory already exists.
- `frame_output_failed` — Neither artifact could be preserved after frame output failed.
- `partial_output` — Only one artifact completed; inspect `partialOutput` and `recoveryHint`.

**Known limitation:** Recording an element inside a windowed popup may capture the underlying window. Record the whole window or use `ui screenshot --capture-screen`. See [#646](https://github.com/microsoft/winappCli/issues/646).


Programmatically activate an element (click button, toggle checkbox, expand combo box).
```bash
winapp ui invoke btn-submit-7a90 -a myapp             # by slug from inspect
winapp ui invoke btn-submit-a1b2 -a myapp  # by slug from inspect/search
winapp ui invoke cmb-sizecombobox-b4c5 -a myapp # expand combo box
```

Tries patterns in order: InvokePattern → TogglePattern → SelectionItemPattern → ExpandCollapsePattern.

### click
Click an element at its screen coordinates using mouse simulation. Use this for controls that don't support `InvokePattern` (e.g., column headers, list items).
```bash
winapp ui click btn-column1-a3f2 -a myapp              # single click by slug
winapp ui click "Column1" -a myapp                      # single click by text search
winapp ui click btn-column1-a3f2 -a myapp --double      # double-click
winapp ui click btn-column1-a3f2 -a myapp --right       # right-click
```

> Like the other input-injecting verbs, `click` brings the target to the foreground and **fails fast** (`no_interactive_desktop` on a locked/secure desktop, `foreground_not_target` if focus couldn't be transferred) rather than clicking the wrong window. It also **re-resolves the element just before the button-down**: after positioning the cursor it does one final position check, so a continuously moving/animating target fails with **`target_moved`** instead of reporting success after the click landed on empty space — a reported success means the target was still in place when the button went down.

### drag
Press the mouse button at one point, move to another, then release with `drag <from> <to>`, where each endpoint is either an **element selector** (drags from/to the element's center) or **screen coordinates `x,y`** exactly as reported by `winapp ui inspect`. Mix and match freely (selector→selector, selector→coords, coords→coords).

Uses `SendInput` with intermediate moves so the app sees a realistic stream of `WM_MOUSEMOVE` messages. Use it for reorder/resize handles, sliders, canvas drawing, and drag-and-drop.
```bash
winapp ui drag itm-card-9f8e itm-slot-2c1a -a myapp           # reorder: card center → slot center
winapp ui drag itm-card-9f8e 300,400 -a myapp                 # element center → screen coords (from inspect)
winapp ui drag 120,200 480,200 -a myapp                       # raw screen coords → screen coords
winapp ui drag itm-card-9f8e itm-trash-0001 -a myapp --right  # right-button drag

# Press-and-hold / long-press and drop-target dwell
winapp ui drag tile-photo-7b3c tile-photo-7b3c -a myapp --hold-ms 600   # long-press: from == to, hold 600ms, no move
winapp ui drag itm-card-9f8e pane-left-2c1a -a myapp --dwell-ms 350      # settle on the drop target before releasing
```

**Options:**
- `--right` — Drag with the right mouse button instead of the left button.
- `--hold-ms <ms>` — Hold the button down at the start before moving (default: 0). With `<from> == <to>` (no movement) this performs a **press-and-hold / long-press** gesture.
- `--dwell-ms <ms>` — Dwell at the destination after moving, before releasing (default: 0). Lets **drop targets / merge overlays** that arm from a sustained hover (rather than the instant the cursor arrives) latch before the button-up.

> Bare `x,y` are screen coordinates in the same space `winapp ui inspect`/`search` report, and a selector resolves to the element's center — inspect first to pick points.

> Like `send-keys --via send-input`, `drag` injects OS-wide at screen coordinates after bringing the target to the foreground. If focus can't be brought to the target (e.g. focus-stealing prevention from a background process), the command **fails (`foreground_not_target`)** rather than dragging on the wrong window — focus or click the window first. On a locked/secure desktop it fails with **`no_interactive_desktop`**. Each element endpoint is **re-resolved immediately before the drag**; if it's still moving/resizing (an animating target), the command fails with **`target_moved`** instead of dragging to a stale point. (Bare `x,y` endpoints can't be re-verified, so they're used as-is.)

### touch
Inject synthetic **touch** gestures using the Windows pointer-injection API. The contact anchor is either an **element selector** (uses the element's center) or an explicit **screen coordinate `x,y`** via `--at` (same space `winapp ui inspect` reports). Use it for tap/press interactions and multi-touch gestures that mouse simulation can't express.
```bash
winapp ui touch btn-ok-1a2b -a myapp                                   # tap at the element center
winapp ui touch -a myapp --at 320,240                                  # tap at explicit screen coords
winapp ui touch tile-photo-7b3c -a myapp --gesture long-press --hold-ms 600
winapp ui touch -a myapp --at 100,300 --gesture swipe --to-point 400,300
winapp ui touch img-map-9f8e -a myapp --gesture pinch --distance 200    # pinch-to-zoom out (2 fingers)
winapp ui touch img-map-9f8e -a myapp --gesture stretch --distance 200  # stretch-to-zoom in (2 fingers)
```

**Options:**
- `--gesture <g>` — `tap` (default), `double-tap`, `long-press`, `swipe`, `pinch`, `stretch`.
- `--at <x,y>` — Explicit start point (screen coordinates). Defaults to the selector's element center.
- `--to-point <x,y>` — End point for a `swipe`. Takes precedence over `--direction`.
- `--direction <right|left|up|down>` — Swipe direction (default: `right`). Combined with `--distance` to compute the end point when `--to-point` is not given.
- `--distance <px>` — Finger spread for `pinch`/`stretch`, or swipe distance in pixels.
- `--hold-ms <ms>` — Hold contacts down before lifting (long-press hold time; defaults to 500 ms for `long-press` when not set).
- `--duration-ms <ms>` — Glide time for moving gestures (swipe/pinch/stretch; default 300).
- `--fingers <n>` — Number of contacts (1–10; default 1). `pinch`/`stretch` always use 2.

> **Injection safety.** `touch` refuses to inject unless a **non-zero target window handle** resolves and that window holds the foreground — it fails with **`no_target`** when no window can be resolved, **`foreground_not_target`** if focus couldn't be transferred, or **`no_interactive_desktop`** on a locked/secure desktop. Every coordinate (element center, explicit `--at`/`--to-point`, and generated waypoints) is **checked against the target window rectangle**; a point outside the window is surfaced as a **non-fatal warning** (a `warnings[]` entry in `--json`, or a warning line in text mode) and injection still proceeds — matching the mouse verbs (`click`/`drag`/`hover`/`scroll`), which also inject at out-of-window coordinates. `--fingers` above 10 is rejected up front.
>
> **Hardware note.** Touch prefers the modern synthetic-pointer device (`CreateSyntheticPointerDevice(PT_TOUCH)`) and falls back to the legacy `InitializeTouchInjection`/`InjectTouchInput` API. If injection is unsupported on the current device/session, the command surfaces the **actual Win32 error code** (e.g. "unsupported") rather than reporting a false success — treat a non-zero exit as "touch not delivered".
>
> **Remote Desktop / VM sessions.** In a Remote Desktop (RDP) or some VM sessions the OS may accept synthetic touch (exit 0) without it actually reaching the target app. When a remote session is detected, `touch` appends a **delivery-uncertainty warning** — a `warnings[]` entry in `--json`, or a warning line in text mode. A ✅/exit 0 then means the injection *call* succeeded, **not** that the app received the input; confirm the effect with `ui screenshot`/`ui inspect` when it matters.

### pen
Inject synthetic **pen/stylus** input — taps and ink strokes — using the Windows synthetic-pointer API (`CreateSyntheticPointerDevice(PT_PEN)`; Windows 10 1809+). Target an element center, an explicit `--at` point, or a full `--path` ink stroke.
```bash
winapp ui pen canvas-1a2b -a myapp                                     # pen tap at the element center
winapp ui pen -a myapp --at 320,240 --pressure 0.8                     # firm pen tap at explicit coords
winapp ui pen -a myapp --path "100,100 150,120 210,140 260,120"        # draw an ink stroke
winapp ui pen -a myapp --path "100,100 260,100" --eraser               # erase along a stroke
winapp ui pen -a myapp --at 200,200 --tilt-x 30 --tilt-y -15           # tilted pen contact
```

**Options:**
- `--at <x,y>` — Pen contact point (screen coordinates). Defaults to the selector's element center. Ignored when `--path` is given.
- `--path "<x,y x,y …>"` — Ink stroke path as whitespace-separated `x,y` pairs (a one-point path is a tap).
- `--pressure <0.0–1.0>` — Pen pressure (default 0.5).
- `--tilt-x <deg>` / `--tilt-y <deg>` — Pen tilt angles, −90 to 90 (default 0).
- `--eraser` — Use the eraser end of the pen instead of the tip.
- `--duration-ms <ms>` — Total stroke travel time in milliseconds distributed as interpolated UPDATE frames across the path (default: ~10 ms per waypoint). Use this to control how fast the pen visibly moves from start to end.

> **Injection safety.** Like `touch`, `pen` refuses to inject without a **non-zero, foregrounded target window** (`no_target` / `foreground_not_target` / `no_interactive_desktop`) and **checks every ink point** against the target window rectangle, surfacing any out-of-window coordinate as a **non-fatal warning** (`warnings[]` in `--json`, or a warning line in text mode) while still injecting — consistent with the mouse verbs. Invalid `--pressure` (outside 0.0–1.0) or tilt (outside ±90°) are rejected up front.
>
> **Remote Desktop / VM sessions.** Pen routing is **especially unreliable over Remote Desktop**: the injection call can report success (exit 0) while **no pen input reaches the app**. When a remote session is detected, `pen` appends a **delivery-uncertainty warning** (`warnings[]` in `--json`, or a warning line in text mode) so a ✅ is not mistaken for confirmed delivery. Validate pen-dependent flows on a **local, interactive desktop**.

### hover
Move the mouse to an element's center to trigger hover effects (tooltips, flyouts, visual states). Uses `SendInput` for realistic mouse movement with a small wiggle, then waits for a configurable dwell time.
```bash
winapp ui hover btn-info-a1b2 -a myapp                          # hover with default 800ms dwell
winapp ui hover btn-info-a1b2 -a myapp --dwell-time 1200        # longer dwell for slow tooltips
winapp ui hover btn-info-a1b2 -a myapp; winapp ui screenshot -a myapp --capture-screen  # hover then capture tooltip
```

**Options:**
- `--dwell-time <ms>` — Time in milliseconds to wait after hovering for effects to appear (default: 800, range: 0–10000)

### send-keys
Send synthetic keyboard input — the keyboard counterpart to `click`. UIA has no keyboard-injection pattern, so this drops to the Win32 layer. Use it for keyboard navigation (arrows, Tab, Enter, Esc), shortcuts (`ctrl+c`, `alt+f4`), and typing into controls that need per-keystroke events rather than `set-value`'s atomic write.
```bash
winapp ui send-keys "down down enter" -a myapp                 # arrow navigation then commit
winapp ui send-keys "ctrl+a delete" -a myapp                   # select all, then delete
winapp ui send-keys "Hello world" --target txt-name-a1b2 -a myapp  # focus a field, then type text
winapp ui send-keys "text=down text=down text=enter" -a myapp  # type the words, don't press the keys
winapp ui send-keys "down down enter" -a myapp --verbatim      # same, but type the whole argument literally
winapp ui send-keys "alt+f4" -a myapp                          # close window via accelerator
winapp ui send-keys "vk=0x5D" -a myapp                         # a key with no friendly name (Apps/Menu key)
winapp ui send-keys "ctrl+shift+t" -a myapp --via send-input   # use OS-wide injection instead of PostMessage
winapp ui send-keys "win+shift+v" -a myapp --via send-input --allow-system-keys  # opt in to drive a global hotkey
```

**Key grammar** (whitespace-separated tokens, quote multi-token strings):
- **Named keys** — `enter`/`return`, `tab`, `esc`/`escape`, `space`, `backspace`, `delete`/`del`, `insert`, `home`, `end`, `pageup`/`pgup`, `pagedown`/`pgdn`, `up`/`down`/`left`/`right`, `f1`–`f16`, `apps`, `printscreen`, `capslock`.
- **Sequences** — multiple tokens are pressed in order: `down down enter`.
- **Modifier combos** — `ctrl`, `shift`, `alt`, `win` joined with `+`: `ctrl+shift+t`, `alt+f4`.
- **Literal text** — any token that isn't a known key is typed character by character: `hello`. Adjacent literal words keep the space between them, so a quoted phrase like `"Hello world"` is typed verbatim (the space is preserved); a literal that merely contains `+` such as `C++` or `a+b` is typed as text, not parsed as a combo.
- **Explicit literal escape** — prefix a token with `text=` to type it verbatim even when it collides with a key or modifier name: `text=enter` types the word "enter" instead of pressing Enter, and `text=ctrl+a` types the literal string. Mirrors the `vk=` escape; the escaped value still coalesces with adjacent literal words (`text=down low` → "down low"). Because tokens are whitespace-split (and adjacent literals re-join with a single space), use **backslash escapes inside a `text=` value** to type whitespace that wouldn't otherwise survive: `\s` → space, `\t` → tab, `\n` → newline, `\r` → newline, `\\` → literal backslash. `\n`, `\r`, and `\r\n` each insert a single line break (an Enter / `VK_RETURN`), so `text=line1\nline2` and `text=line1\r\nline2` both type one newline. So `text=a\s\sb` types "a  b" (double space), and `text=\shi` keeps a leading space. An unrecognised escape (e.g. `\x`) is left verbatim.
- **Whole-argument literal (`--verbatim`)** — when the *entire* payload is literal text, pass `--verbatim` instead of escaping every token with `text=`. It types the whole keys argument exactly as given — no named-key/combo/`vk=`/`text=` interpretation — and, unlike the normal path, preserves exact internal whitespace (no collapsing) without needing `\s`. So `send-keys "down down enter" --verbatim` types the words, and `send-keys "a  b" --verbatim` keeps the double space. Backslash escapes are **not** decoded in `--verbatim` mode (a `\s` is typed as a backslash and an "s"); use a `text=` token when you need an escaped control character.
- **Raw virtual keys** — `vk=0xNN` (hex) or `vk=NN` (decimal) for keys without a friendly name.

**Options:**
- `--target <selector>` — Focus this element (via UIA) before sending keys. Without it, keys go to the app's currently focused element.
- `--verbatim` — Type the entire keys argument as literal text (no key/combo/`vk=`/`text=` parsing) and preserve exact whitespace. The whole-argument form of the per-token `text=` escape.
- `--via <transport>` — `post-message` (default) posts `WM_KEYDOWN`/`WM_KEYUP`/`WM_CHAR` to the target window's queue. It is HWND-targeted and bypasses UIPI (works across integrity levels). `send-input` injects OS-wide via `SendInput` and goes to the foreground window.

**Choosing a transport / known limits:**
- `post-message` is the default because it bypasses UIPI and doesn't depend on the window being foreground. Limits: it cannot trigger global hotkeys registered through `WH_KEYBOARD_LL` low-level hooks (those tap input upstream of any window queue), and apps that read raw key state via `GetAsyncKeyState` may not observe held modifiers. It automatically resolves and posts to the target thread's **focused child window** (via `GetGUIThreadInfo`) after foregrounding, so classic Win32/WinForms apps whose controls are separate child windows receive keys without manually targeting the control. **WinUI 3 / UWP apps have windowless XAML controls with no child HWND**, so a posted `WM_CHAR`/`WM_KEYDOWN` has nothing to land in and is dropped — post-message can't drive them (the command warns and exits 0); use `--via send-input`. (WPF windows are single-HWND and route keys to the internally focused element, so post-message works there.)
- `send-input` produces fully real input (modifiers visible to `GetAsyncKeyState`, fires low-level hooks) but goes to whatever window is foreground and is **blocked by UIPI when injecting from an elevated process into a lower-integrity (AppContainer/AppX) target**. If `send-input` reports a failure, the target is likely elevated or an AppX app — use `post-message`, or run the CLI at a matching integrity level. As a safety guard, `send-input` verifies the target window is actually in the foreground immediately before injecting and **fails (`foreground_not_target`) rather than typing into the wrong window** if focus could not be brought to it — focus or click the window first. On a **locked or secure desktop** it instead fails with **`no_interactive_desktop`** (no foreground window exists to inject into) — unlock the session, or use a UIA-pattern verb (`set-value`, `invoke`).
- **System-reserved combos** (`win+l`, `win+r`, `ctrl+shift+esc`, `ctrl+alt+del`, `alt+tab`, `alt+f4`, `ctrl+esc`, lone `win`/`printscreen`, …) act on the OS/shell rather than just the target when sent OS-wide. `send-input` **rejects them by default** (errors with `invalid_arguments` and sends nothing) because injecting them at the OS level has effects well beyond the target window (e.g. `win+l` would lock the session). Pass **`--allow-system-keys`** to opt in — this lets you drive a global hotkey such as PowerToys' `win+shift+v` or `win+r` (the global low-level hook watches the OS-wide input stream, so the injected combo fires it). **Exceptions that stay blocked even with `--allow-system-keys`:** `win+l` locks the workstation via `LockWorkStation()` which is unrecoverable from automation (breaks CI and remote-desktop sessions), and `ctrl+alt+del` is a Secure Attention Sequence (SAS) that Windows drops from injected input regardless of the flag — it can never take effect, so it errors (`invalid_arguments`, exit 1) instead of reporting a misleading success. Other combos (`alt+f4`, `ctrl+shift+esc`, `win+r`, …) become allowed with the flag — caller beware. Alternatively, to deliver a system combo to a specific window use `--via post-message`, which is window-scoped and unaffected (a posted `win+l` is harmless, though a posted `alt+f4` still closes the target window).

**Per-keystroke events (KeyDown / TextChanged):**
- **Named keys and modifier combos** (`down`, `enter`, `ctrl+shift+t`, `vk=0xNN`) fire a real `KeyDown` (and `KeyUp`) on **both** transports — they're delivered as discrete `WM_KEYDOWN`/`WM_KEYUP` (or `SendInput` virtual-key events).
- **Literal typed text** (`hello`) differs by transport:
  - `--via send-input` maps each character to its virtual key (plus Shift) on the active keyboard layout, so the target sees a genuine **`KeyDown` with the correct virtual key** followed by the OS-composed `WM_CHAR` (raising **`TextChanged`**) — i.e. one full keystroke per character. Characters not reachable on the current layout (or needing Ctrl/AltGr) fall back to a Unicode packet so the exact character still lands. **Use `send-input` when you need per-keystroke `KeyDown` fidelity** (e.g. driving a WinUI 3 / WPF `TextBox` whose handlers key off `KeyDown`). For a normal (non-elevated) WinUI 3 test host, bring its window to the foreground first (`winapp ui focus` / clicking it) since `send-input` targets the foreground window.
  - `--via post-message` posts a single `WM_CHAR` per character (it does **not** post `WM_KEYDOWN`/`WM_KEYUP` for typed text — those are reserved for named keys/combos), which does **not** raise a per-character `KeyDown`. It automatically retargets to the window's **focused child control**, so classic Win32/WinForms `WM_CHAR`-driven edit controls land the text (raising `TextChanged`). **Caveat:** WinUI 3 / UWP / XAML apps (winapp's primary target) have **windowless** controls that ignore posted `WM_CHAR`/`WM_KEYDOWN` — so *neither* literal text *nor* named keys (Enter, digits, …) reach them, even though the command reports success. It emits a warning when the target looks like XAML and still exits 0 (`PostMessage` is fire-and-forget and can't confirm delivery). **Use `--via send-input` to drive WinUI 3 / UWP / WPF apps**; reserve `post-message` for classic Win32 controls or when you only need it window-scoped across integrity levels.

**JSON output (`--json`):** the result's `hwnd` is the **effective** window the keys were delivered to — for `--via post-message` this is the resolved **focused child control** when the command retargets to it (not necessarily the top-level `-w`/`-a`/`-e` window), so automation can confirm exactly where input landed. When that effective target looks like a windowless XAML host, the delivery caveat above is also surfaced as a `warnings[]` entry (the same advisory shown on the console), so a ✅ exit 0 isn't mistaken for confirmed delivery.

### set-value
Set a value on an editable element **programmatically** (no keystrokes, no app foreground). Uses a fallback chain:
1. **ValuePattern** — TextBox, ComboBox, PasswordBox, and most editable controls.
2. **RangeValuePattern** — numeric controls (Slider, ProgressBar) when the value parses as a number.
3. **LegacyIAccessible** (`IAccessible::put_accValue`) — the fallback for **TextPattern-only** edit controls that expose no ValuePattern (e.g. rich-edit / `Document` compose boxes). This closes the read/write gap where `get-value` could read such a control but `set-value` could not.
```bash
winapp ui set-value txt-textbox-a4b1 "Hello world" -a notepad
winapp ui set-value sld-volume-b2c3 75 -a myapp
winapp ui set-value doc-compose-9f3a "hello" -a myapp        # RichEdit/compose box via LegacyIAccessible
```
If none of the three patterns can set the value, `set-value` fails with a clear error pointing at `send-keys` as the last resort.

> **Not every rich editor supports programmatic set.** The LegacyIAccessible fallback only works on controls whose accessibility implements `IAccessible::put_accValue` — native Win32 rich-edit controls and Chromium/Electron/WebView2 compose surfaces typically do. **WinUI 3 `RichEditBox` and WPF `RichTextBox` don't support programmatic value-setting** — by design they expose their contents to UI Automation as read-only (Text pattern, no settable Value pattern), so `set-value` can't write to them. Use `send-keys` (which needs an unlocked, foregrounded desktop) for those.


### get-value
Read the current value from an element. Uses a smart fallback chain: TextPattern (RichEditBox, Document) → ValuePattern (TextBox, Slider) → SelectionPattern (ComboBox, RadioButton, TabView) → Name (labels).
```bash
winapp ui get-value doc-texteditor-53ad -a notepad          # read full document text
winapp ui get-value SearchBox -a myapp                      # read TextBox content
winapp ui get-value CmbTheme -a myapp                       # read ComboBox selected item
winapp ui get-value sld-volume-b2c3 -a myapp                # read Slider value
winapp ui get-value lbl-title-a1b2 -a myapp --json          # JSON: { "elementId": "...", "text": "..." }
```

### focus
Move keyboard focus to an element.
```bash
winapp ui focus txt-textbox-a4b1 -a notepad
```

### scroll-into-view
Scroll an element into the visible area.
```bash
winapp ui scroll-into-view itm-targetitem-c3d4 -a myapp
```

### wait-for
Wait for an element to appear, disappear, or have a value reach a target.
```bash
winapp ui wait-for Button -a myapp --timeout 5000                       # wait for any button
winapp ui wait-for btn-submit-7a90 -a myapp --timeout 5000             # wait for specific element
winapp ui wait-for CounterDisplay -a myapp --value "5" --timeout 5000  # wait for element value (smart fallback)
winapp ui wait-for lbl-status -a myapp --property Name --value "Done" --timeout 5000  # wait for specific property
winapp ui wait-for btn-submit-a1b2 --gone -a myapp --timeout 2000      # wait for element to disappear
winapp ui wait-for lbl-status -a myapp --value "Done" --contains       # substring match instead of exact equality
```

### scroll
Scroll a container element. Find scrollable containers with `search scroll` — look for `[scroll:v]` (vertical) or `[scroll:h]` (horizontal) markers.
```bash
# Find which elements are scrollable and in which direction
winapp ui search scroll -a myapp
#   pn-scrollview-bfef Pane "scrollView" [scroll:v] (main content, vertical)
#   pn-scrollviewer-bfb1 Pane "scrollViewer" [scroll:h] (horizontal list)

# Scroll the main content down
winapp ui scroll pn-scrollview-bfef --direction down -a myapp

# Jump to top/bottom
winapp ui scroll pn-scrollview-bfef --to bottom -a myapp

# If you target an element that's not scrollable, scroll walks up to find the nearest scrollable parent
winapp ui scroll itm-someitem-a1b2 --direction down -a myapp

# Synthesize real mouse-wheel input over the element (1 = one notch up, -1 = one notch down).
# Use this to test handlers that respond to the wheel directly (zoom, custom scroll) rather than ScrollPattern.
winapp ui scroll img-map-a1b2 --wheel -1 -a myapp
```

**Options:**
- `--direction <up|down|left|right>` — Scroll incrementally via `ScrollPattern`.
- `--to <top|bottom>` — Jump to the start/end via `ScrollPattern`.
- `--wheel <notches>` — Synthesize mouse-wheel input over the element's center via `SendInput`, in wheel notches (detents): `1` = one notch up/away, `-1` = one notch down/toward, `3` = three notches up. (Each notch is the Windows `WHEEL_DELTA` of 120 units that `SendInput` consumes; the CLI scales notches by 120 for you.) Bypasses `ScrollPattern`.

> `--direction`, `--to`, and `--wheel` are mutually exclusive — pass exactly one. Because `--wheel` injects OS-wide input at screen coordinates, it brings the target to the foreground first and **fails (`foreground_not_target`)** if focus couldn't be transferred, rather than scrolling the wrong window.

### get-focused
Show the element that currently has keyboard focus.
```bash
winapp ui get-focused -a myapp
```

### list-windows
List all visible windows for an app, including popups and dialogs.
By default, untitled windows with zero size (invisible system windows) are excluded.
```bash
winapp ui list-windows -a imageresizer
winapp ui list-windows -a Terminal
winapp ui list-windows                                      # all windows (no filter)
winapp ui list-windows --show-hidden                        # include invisible zero-size windows
```

## Framework Support

| Framework | inspect | search | invoke | set-value | screenshot |
|---|---|---|---|---|---|
| **WPF** | ✅ Full tree | ✅ All properties | ✅ All patterns | ✅ ¹ | ✅ |
| **WinForms** | ✅ | ✅ | ✅ | ✅ | ✅ |
| **Win32** | ✅ | ✅ | ✅ | ✅ | ✅ |
| **WinUI 3** | ✅ | ✅ | ✅ | ✅ ¹ | ✅ |
| **Electron** | ⚠️ Chromium tree | ⚠️ Limited | ⚠️ Varies | ⚠️ Varies | ✅ |
| **Flutter** | ⚠️ Basic | ⚠️ Basic | ❌ Minimal | ❌ | ✅ |

¹ `set-value` works on any control exposing ValuePattern/RangeValuePattern, plus TextPattern-only edit controls whose accessibility implements `IAccessible::put_accValue` (LegacyIAccessible fallback). **WinUI 3 `RichEditBox` and WPF `RichTextBox` are exceptions** — they expose only the read-only Text pattern (no settable Value pattern), so they can't be set programmatically by design; use `send-keys` (interactive desktop required) to type into them.

## Using the engine from your own code

Everything `winapp ui` does is available as a library, so you can drive the same automation from a
test or tool without shelling out to the CLI:

| Package | What it adds |
|---|---|
| `Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation` | Inspection, selectors, UIA pattern interaction, input injection, screenshots |
| `Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Recording` | Video recording to MP4, plus frame bundles |

```csharp
var services = new ServiceCollection().AddLogging().AddWinAppUiAutomation().BuildServiceProvider();
var ui = services.GetRequiredService<IUiAutomation>();

var target = UiTarget.FromWindowHandle(myWindowHandle);
var save = await ui.FindSingleElementAsync(target, new UiSelector { Query = "Save" }, default);
await ui.InvokeAsync(target, save!, default);
```

Recording is a separate package so that projects which only inspect and drive UI don't pull in
SkiaSharp. The automation package targets both `net10.0-windows` and
`net10.0-windows10.0.19041.0`; the latter adds Windows Graphics Capture, which is what lets
`screenshot` capture occluded or GPU-composited windows. See each package's README on NuGet for the
full API and the target-framework trade-off.

`UiTarget.FromWindowHandle` is the entry point for test frameworks that already hand you a window —
for example `MSTest.Windows.UIAutomation`, whose `WindowTest.MainWindow` is a UIA2
`AutomationElement` you bridge across with `MainWindow.Current.NativeWindowHandle`.

## Troubleshooting

| Error | Cause | Solution |
|---|---|---|
| "No running app found" | App not running or name mismatch | Check process name or use PID |
| "Multiple windows match" | Ambiguous `-a` value | Use `-w <HWND>` from the listed options |
| "has multiple windows" | Process has multiple windows | Use `-w <HWND>` to target specific one |
| "Selector matched N elements" | Ambiguous legacy selector | Use slugs from `inspect` output, or append `[0]`, `[1]` to legacy selectors |
| "Element may have changed" | Slug hash doesn't match current element | Re-run `inspect` or `search` to get fresh slugs |
| "does not support any invoke pattern" | Element can't be invoked | Use `inspect` on the element to find an invokable child |
| "No UIA window found" | UIA can't see the process | Use `list-windows` to find the HWND, then `-w` |
| "Window has zero size" | Window is minimized | App will be auto-restored |
| Popup/dropdown not in screenshot | Default capture is per-window and doesn't include unowned overlays | Use `--capture-screen` flag |
| `element_not_found` during record | Selector given but no matching element | Re-run `inspect` or `search` to get a fresh selector |
| WGC unavailable during record | WGC capture init failed; no silent fallback | Check GPU/driver; use `--capture-screen` to consent to screen-DC capture |

## Common Patterns

### Navigate and verify
```bash
winapp ui invoke btn-settings-a1b2 -a myapp          # click a button
winapp ui wait-for pn-settingspage-c3d4 -a myapp    # wait for page to load
winapp ui screenshot -a myapp --output settings.png  # verify visually
```

### Find text and invoke its parent
```powershell
# Search shows invokable ancestor; invoke auto-walks to it
winapp ui invoke 'Save changes' -a myapp

# Or search first to see what matches, then invoke
winapp ui search "Save changes" -a myapp; winapp ui invoke btn-save-c3d4 -a myapp
```

### Disambiguate duplicate elements
```powershell
winapp ui search '#Image' -a myapp; winapp ui invoke itm-image-a2b3 -a myapp
```

### Screenshot with popup overlays
```powershell
winapp ui set-value txt-searchbox-e5f6 "query" -a myapp; winapp ui screenshot -a myapp --capture-screen
```

### Navigate, wait, and verify (single chain)
```powershell
winapp ui invoke btn-settings-a1b2 -a myapp; winapp ui wait-for pn-settingspage-c3d4 -a myapp --timeout 3000; winapp ui screenshot -a myapp -o settings.png
```

### Discover, click, and verify
```powershell
winapp ui inspect -a myapp --interactive; winapp ui invoke btn-submit-7a90 -a myapp; winapp ui screenshot -a myapp
```

### File dialog interaction
File open/save dialogs are standard Windows dialogs with UIA support:
```powershell
# Trigger the dialog, find it, type the path, confirm
winapp ui invoke btn-openfilebtn-a2b3 -a myapp
winapp ui list-windows -a myapp                                      # find dialog HWND
winapp ui set-value txt-1148-c4d5 "C:\path\to\file.png" -w <dialog-hwnd>
winapp ui invoke btn-open-e6f7 -w <dialog-hwnd>
```
Use `inspect -w <dialog-hwnd> --interactive` to discover the actual slugs for a specific dialog.

### Why `;` for chaining (not `&&`)
PowerShell's `&&` operator can freeze when a native CLI writes to stderr or uses ANSI escape sequences. Use `;` instead — it runs each command unconditionally and avoids this deadlock. This is also better for agent workflows: you usually want the screenshot to run even if the invoke had a non-zero exit.

## CI Testing Patterns

Use `winapp ui` commands in CI pipelines (GitHub Actions, Azure DevOps) for smoke tests
and UI validation. `wait-for` with `--property` and `--value` acts as an assertion —
it returns exit code 1 on timeout, failing the CI step automatically.

### Launch and test in GitHub Actions
```yaml
steps:
  - name: Build
    run: dotnet build MyApp.csproj -c Debug -p:Platform=x64

  - name: Launch and test
    run: |
      $result = winapp run .\bin\x64\Debug\net8.0-windows10.0.26100.0\win-x64 --detach --json | ConvertFrom-Json
      $appPid = $result.ProcessId

      # Wait for window to initialize
      winapp ui wait-for "Main Window" -a $appPid --timeout 30000

      # Run tests — each wait-for exits non-zero on failure
      winapp ui invoke "Login" -a $appPid
      winapp ui wait-for "Dashboard" -a $appPid --timeout 10000
      winapp ui screenshot -a $appPid -o dashboard.png
```

### Assert element state with `wait-for`
`wait-for --value` polls until an element's value matches the expected string, using the same
smart fallback as `get-value` (TextPattern → ValuePattern → SelectionPattern → Name). Returns exit code 0 on match,
exit code 1 on timeout — making it a CI-friendly assertion. Use `--property` to check a specific
UIA property instead.

```bash
# Assert: button click updated the counter (smart value fallback — works for TextBlock, TextBox, etc.)
winapp ui invoke "Counter Button" -a $pid
winapp ui wait-for "Counter Display" -a $pid --value "Count: 1" -t 5000

# Assert: text input was accepted
winapp ui set-value "Search Box" "hello world" -a $pid
winapp ui wait-for "Search Box" -a $pid --value "hello world" -t 3000

# Assert: checkbox was toggled (use --property for specific UIA properties)
winapp ui invoke "Dark Mode" -a $pid
winapp ui wait-for "Dark Mode" -a $pid --property ToggleState --value "On" -t 3000

# Assert: navigation happened (new page appeared)
winapp ui invoke "Settings" -a $pid
winapp ui wait-for "Settings Page" -a $pid -t 10000

# Assert: dialog was dismissed (element disappeared)
winapp ui invoke "Close" -a $pid
winapp ui wait-for "Dialog Title" -a $pid --gone -t 5000
```

### Assert with JSON output
Use `--json` with PowerShell or jq for more complex assertions:

> **Exit-code contract for `search` and `wait-for` in `--json` mode:** when no element matches
> (`search`) or the wait times out (`wait-for`), the command writes a fully parseable result envelope
> to **stdout** (`{ "matchCount": 0, ... }` or `{ "found": false, "timedOut": true, ... }`) and
> returns **exit code 1**. Stderr is empty in `--json` mode (logger output is suppressed).
> Branch on the envelope fields, or on `$LASTEXITCODE`, depending on which is more ergonomic.

```powershell
# Assert: search found exactly one match
$result = winapp ui search "Submit" -a $pid --json | ConvertFrom-Json
if ($result.matchCount -ne 1) { throw "Expected 1 Submit button, found $($result.matchCount)" }

# Assert: element has expected properties
# inspect --json returns { windows: [{ hwnd, title, elements: [...] }] };
# each window's elements[] is the nested tree (children rendered via .children).
$tree = winapp ui inspect "Counter Display" -a $pid --json | ConvertFrom-Json
$counter = $tree.windows[0].elements[0]
if ($counter.name -ne "Count: 3") { throw "Counter value wrong: $($counter.name)" }
```

### Full smoke test example
```powershell
# Launch
$app = winapp run .\build-output --detach --json | ConvertFrom-Json

# Verify app loaded
winapp ui wait-for "Main Page" -a $app.ProcessId -t 30000

# Interact and assert
winapp ui invoke "Add Item" -a $app.ProcessId
winapp ui set-value "Item Name" "Test Item" -a $app.ProcessId
winapp ui invoke "Save" -a $app.ProcessId
winapp ui wait-for "Test Item" -a $app.ProcessId -t 5000              # assert item appeared in list
winapp ui wait-for "Save" -a $app.ProcessId --gone -t 3000            # assert save dialog closed

# Visual verification
winapp ui screenshot -a $app.ProcessId -o smoke-test.png
```
