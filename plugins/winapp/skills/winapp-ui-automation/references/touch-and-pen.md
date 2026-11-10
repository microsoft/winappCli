# Touch and pen input

## Touch gestures (tap, swipe, pinch, stretch, long-press)
Inject synthetic touch. The contact anchor is an element selector (its center) or an explicit `--at x,y` screen coordinate. Prefers the modern synthetic-pointer device and falls back to the legacy touch-injection API.
```powershell
# Tap an element center; or tap explicit screen coordinates
winapp ui touch btn-ok-1a2b -a myapp
winapp ui touch -a myapp --at 320,240

# Long-press, swipe, and two-finger pinch/stretch (zoom)
winapp ui touch tile-photo-7b3c -a myapp --gesture long-press --hold-ms 600
winapp ui touch -a myapp --at 100,300 --gesture swipe --to-point 400,300
winapp ui touch img-map-9f8e -a myapp --gesture pinch --distance 200
winapp ui touch img-map-9f8e -a myapp --gesture stretch --distance 200 --angle 90
```
- Gestures: `tap` (default), `double-tap`, `long-press`, `swipe`, `pinch`, `stretch`. `--fingers` 1–10 (pinch/stretch always 2). `--angle` 0–359 sets the pinch/stretch finger axis (0 horizontal, 90 vertical). Refuses without a non-zero foregrounded target (`no_target`/`foreground_not_target`/`no_interactive_desktop`); every coordinate is checked against the target window, and a point outside it produces a non-fatal warning (`warnings[]` in `--json`) while injection still proceeds — consistent with the mouse verbs. If injected touch is unsupported on the device, the command surfaces the real Win32 error rather than a false success. In a Remote Desktop/VM session delivery isn't guaranteed even on exit 0 — the command appends a delivery-uncertainty warning (`warnings[]` in `--json`); verify the effect with a screenshot.

## Pen / stylus (taps and ink strokes)
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
