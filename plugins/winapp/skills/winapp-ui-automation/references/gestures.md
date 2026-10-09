# Drag, touch, pen, and scrolling

### Drag (reorder, resize, sliders, drag-and-drop)
Press the mouse button at one point, move to another, then release with `drag <from> <to>`, where each endpoint is an element selector (uses its center) or screen `x,y` coordinates from `ui inspect`. Uses `SendInput` with intermediate moves so apps see a realistic `WM_MOUSEMOVE` stream.
```powershell
# Reorder one item onto another (center → center)
winapp ui drag itm-card-9f8e itm-slot-2c1a -a myapp

# Element center → screen coordinates (as reported by `ui inspect`)
winapp ui drag itm-card-9f8e 300,400 -a myapp

# Raw screen coordinates → screen coordinates
winapp ui drag 120,200 480,200 -a myapp

# Right-button drag
winapp ui drag itm-card-9f8e itm-trash-0001 -a myapp --right
```
- A selector drags from/to the element's center; `x,y` are screen coordinates in the same space `ui inspect`/`search` report. Element endpoints are re-resolved just before the drag and fail with `target_moved` if still animating; on a locked/secure desktop the drag fails with `no_interactive_desktop`.

### Touch gestures (tap, swipe, pinch, stretch, long-press)
Inject synthetic touch. The contact anchor is an element selector (its center) or an explicit `--at x,y` screen coordinate. Prefers the modern synthetic-pointer device and falls back to the legacy touch-injection API.
```powershell
# Tap an element center; or tap explicit screen coordinates
winapp ui touch btn-ok-1a2b -a myapp
winapp ui touch -a myapp --at 320,240

# Long-press, swipe, and two-finger pinch/stretch (zoom)
winapp ui touch tile-photo-7b3c -a myapp --gesture long-press --hold-ms 600
winapp ui touch -a myapp --at 100,300 --gesture swipe --to-point 400,300
winapp ui touch img-map-9f8e -a myapp --gesture pinch --distance 200
winapp ui touch img-map-9f8e -a myapp --gesture stretch --distance 200
```
- Gestures: `tap` (default), `double-tap`, `long-press`, `swipe`, `pinch`, `stretch`. `--fingers` 1–10 (pinch/stretch always 2). Refuses without a non-zero foregrounded target (`no_target`/`foreground_not_target`/`no_interactive_desktop`); every coordinate is checked against the target window, and a point outside it produces a non-fatal warning (`warnings[]` in `--json`) while injection still proceeds — consistent with the mouse verbs. If injected touch is unsupported on the device, the command surfaces the real Win32 error rather than a false success. In a Remote Desktop/VM session delivery isn't guaranteed even on exit 0 — the command appends a delivery-uncertainty warning (`warnings[]` in `--json`); verify the effect with a screenshot.

### Pen / stylus (taps and ink strokes)
Inject synthetic pen input (Windows 10 1809+). Target an element center, an explicit `--at`, or a full `--path` ink stroke, with pressure/tilt/eraser control.
```powershell
# Pen tap at element center; firm tap at explicit coords
winapp ui pen canvas-1a2b -a myapp
winapp ui pen -a myapp --at 320,240 --pressure 0.8

# Draw an ink stroke, or erase along one
winapp ui pen -a myapp --path "100,100 150,120 210,140 260,120"
winapp ui pen -a myapp --path "100,100 260,100" --eraser
```
- `--pressure` 0.0–1.0, `--tilt-x`/`--tilt-y` ±90°, `--eraser` for the eraser end. Same injection safety as `touch`: requires a non-zero foregrounded target window and checks every ink point (out-of-window → non-fatal `warnings[]` advisory, injection still proceeds). Pen routing is especially unreliable over Remote Desktop — exit 0 can mean the call succeeded but no pen reached the app; the command appends a delivery-uncertainty warning (`warnings[]` in `--json`). Validate pen flows on a local interactive desktop.

### Scroll containers
```powershell
# Find scrollable containers — look for [scroll:v] (vertical) or [scroll:h] (horizontal)
winapp ui search scroll -a myapp
# Output:
#   pn-scrollview-bfef Pane "scrollView" [scroll:v] (2127,296 1191x965)
#   pn-scrollviewer-bfb1 Pane "scrollViewer" [scroll:h] (2127,296 1191x216)

# Scroll vertically
winapp ui scroll pn-scrollview-bfef --direction down -a myapp

# Scroll to top/bottom
winapp ui scroll pn-scrollview-bfef --to bottom -a myapp

# Scroll and then inspect for newly visible elements
winapp ui scroll pn-scrollview-bfef --direction down -a myapp; winapp ui search TargetItem -a myapp

# Synthesize real mouse-wheel input over the element (1 = one notch up, -1 = down) — tests wheel handlers (zoom, custom scroll)
winapp ui scroll img-map-a1b2 --wheel -1 -a myapp
```
