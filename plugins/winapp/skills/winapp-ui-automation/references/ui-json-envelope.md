# `winapp ui --json` envelopes

The `--json` output for the `winapp ui` command group uses the envelopes below.
The inspect, search, wait-for, and get-focused envelopes were reshaped in
v0.3.1; the DPI context and typed get-property element are available in v0.6.3+.

## `ui inspect --json`

Top-level shape (elements are now nested under `windows[]`, not flat):

```json
{
  "depth": 0,
  "interactive": false,
  "hideDisabled": false,
  "hideOffscreen": false,
  "windows": [
    {
      "hwnd": 123456,
      "title": "...",
      "className": "...",
      "windowDpi": 144,
      "scale": 1.5,
      "dpiAwareness": "per-monitor-aware",
      "coordinateSpace": "physical-screen-pixels",
      "elementCount": 0,
      "elements": [
        {
          "selector": "btn-save-c3d4",
          "name": "Save",
          "type": "Button",
          "isEnabled": true,
          "isOffscreen": false,
          "x": 100,
          "y": 200,
          "width": 120,
          "height": 32,
          "isInvokable": true
        }
      ]
    }
  ]
}
```

Pre-0.3.1 the shape was `{ "elements": [...] }`. Per-element `id`,
`parentSelector`, and `windowHandle` fields have been **removed** —
`selector` is the public handle.

`windowDpi` is the target window's effective DPI from `GetDpiForWindow(hwnd)`,
not unconditional monitor DPI. `scale` is `windowDpi / 96`.
`dpiAwareness` is `unaware`, `system-aware`, or `per-monitor-aware`:
`GetDpiForWindow` reports 96 for an unaware window, system DPI for a
system-aware window, and the current monitor DPI for a per-monitor-aware
window. If the HWND or DPI context cannot be read, the command fails with an
error instead of substituting 96.

The selected target window remains fail-fast. If a later popup or secondary
window disappears after its UIA tree was collected, that window entry remains
in `windows[]` with a `dpiError` message and without the four DPI context fields;
the other window trees remain available.

Element `x`, `y`, `width`, and `height` values are numbers in physical screen
pixels. `0,0,0,0` is UI Automation's empty/no-displayed-UI rectangle in this
projection. `isOffscreen` is independent: an offscreen element can still have
nonzero bounds.

## `ui inspect --ancestors --json`

Ancestors are now nested as a parent → child chain keyed by `Depth=i`
(previously emitted as sibling roots).

## `ui inspect --interactive`

Non-interactive ancestors are collapsed and surfaced as `ancestorPath` on
surviving descendants. `+more` markers indicate truncated subtrees in both
text and JSON modes.

## `ui get-focused --json`

Always emits an envelope (never a bare value):

- No focus: `{ "hasFocus": false }`
- With focus: `{ "hasFocus": true, "element": { ... } }`

Pre-0.3.1 emitted bare `null` when nothing was focused.

## `ui search --json`

Search returns an envelope, not a bare array:

```json
{
  "matchCount": 1,
  "hasMore": false,
  "matches": [
    {
      "selector": "txt-save-label-a1b2",
      "name": "Save",
      "type": "Text",
      "isEnabled": true,
      "isOffscreen": false,
      "x": 100,
      "y": 200,
      "width": 80,
      "height": 24,
      "isInvokable": false,
      "invokableAncestor": {
        "selector": "btn-save-c3d4",
        "name": "Save button",
        "type": "Button",
        "isEnabled": false,
        "isOffscreen": false,
        "x": 0,
        "y": 0,
        "width": 0,
        "height": 0,
        "isInvokable": true
      }
    }
  ]
}
```

Each match may include an `invokableAncestor` field — itself an
element-shaped object — pointing to the nearest parent that supports
`InvokePattern` (useful when a search hits a non-invokable element
like a label inside a button).

## `ui wait-for --json`

When the condition succeeds:

```json
{
  "found": true,
  "waitedMs": 125,
  "element": {
    "selector": "txt-status-a1b2",
    "name": "Ready",
    "type": "Text",
    "isEnabled": true,
    "isOffscreen": false,
    "x": 100,
    "y": 200,
    "width": 80,
    "height": 24,
    "isInvokable": false
  },
  "timedOut": false
}
```

On timeout, stdout still contains a parseable result and the process exits 1:

```json
{
  "found": false,
  "waitedMs": 5000,
  "timedOut": true
}
```

With `--gone`, success after the element disappears is:

```json
{
  "found": false,
  "waitedMs": 125,
  "timedOut": false
}
```

## `ui get-property --json`

`elementId` and the string-valued `properties` map remain available. The
additive `element` field contains the same scrubbed typed element projection
used by search and wait-for:

```json
{
  "elementId": "btn-save-c3d4",
  "element": {
    "selector": "btn-save-c3d4",
    "name": "Save",
    "type": "Button",
    "isEnabled": true,
    "isOffscreen": false,
    "x": 100,
    "y": 200,
    "width": 80,
    "height": 24,
    "isInvokable": true
  },
  "properties": {
    "Name": "Save",
    "IsEnabled": "True",
    "BoundingRectangle": "100,200,80,24"
  }
}
```

The typed `element` object is the canonical way to consume geometry and boolean
state. The existing `properties` values intentionally remain strings for
backward compatibility.

## `ui status --json`

The resolved target also reports its window DPI context:

```json
{
  "processId": 1234,
  "processName": "MyApp",
  "windowTitle": "My App",
  "hwnd": 123456,
  "windowDpi": 144,
  "scale": 1.5,
  "dpiAwareness": "per-monitor-aware",
  "coordinateSpace": "physical-screen-pixels"
}
```

If a process resolves before it has a top-level window, `hwnd` remains `0` and
the four DPI fields are omitted. A failed DPI read for a nonzero HWND is an
error rather than a silent 96-DPI fallback.

The internal `id`, `parentSelector`, and `windowHandle` fields are
**scrubbed** from typed element results — both at the top level and inside any
nested `invokableAncestor`. Don't depend on them; use `selector` as the handle.

## Error envelope

Every `winapp ui` command writes errors to **stderr** as:

```json
{
  "error": {
    "code": "element_not_found",
    "message": "…",
    "selector": "btn-save-c3d4",
    "details": "…",
    "recoveryHint": "…"
  }
}
```

Only `code` and `message` are always present; the rest are omitted when
they do not apply.

### Desktop coordination

Concurrent `winapp ui` workflows take cooperative turns on the shared
desktop (see the skill's coordination section). Four additional codes can
appear:

| `code` | Meaning |
|---|---|
| `invalid_ui_workflow_id` | `WINAPP_UI_WORKFLOW_ID` is set but empty/whitespace or longer than 256 characters. Fails before any UI side effect. |
| `desktop_coordination_unavailable` | Coordination state could not be read, published, or safely rebuilt — including state written by a newer `winapp`. Mutating commands fail closed rather than acting uncoordinated. |
| `queue_capacity_exceeded` | 64 commands from other workflows are already waiting for the desktop. Counts live foreign waiters, so entries left by commands that exited or were killed do not occupy a slot. |
| `ui_turn_busy` | `ui yield` was run while this same workflow still has a command running or queued, so its turn is not idle. Nothing was released, and the running command is unaffected. Distinct from `invalid_arguments` (the request was well formed) and from `desktop_coordination_unavailable` (coordination is working — this is a valid request at an unsafe moment). Carries a `recoveryHint`: wait for or stop this workflow's other `winapp ui` commands — typically a `record` started with the same `WINAPP_UI_WORKFLOW_ID` — then retry `yield`. |
| `cancelled` | Native Ctrl+C while the command was still waiting for its turn. The command never ran, so it has no UI side effects. Exit code **130**. |

`ui yield` also emits the command-level `invalid_arguments` when `WINAPP_UI_WORKFLOW_ID` is not set
at all — deliberately not `invalid_ui_workflow_id`, which means the variable is present but
malformed. On success it writes `{ "released": true }`, or `{ "released": false }` when this workflow
held nothing to release (both exit **0**).

An npm `AbortSignal` is a different contract: Node force-terminates the child,
so there is usually no envelope and no exit code 130 — the wrapper rejects with
an `AbortError` instead, and UI side effects may already have happened if the
abort landed after the command acquired the desktop.

`cancelled` — and optionally the other coordination errors — carries an
additive `coordination` object:

```json
{
  "error": {
    "code": "cancelled",
    "message": "UI turn wait was cancelled.",
    "coordination": {
      "waitedMs": 1234,
      "queuePosition": 2
    }
  }
}
```

`waitedMs` is always present for a cancellation while queued.
`queuePosition` is one-based among live waiters and is **omitted** when it
cannot be computed reliably — including while a command waits behind its
own workflow's earlier command. Workflow identities are never exposed, in
raw or hashed form.

Cancelling *after* the command acquired its turn keeps that command's
existing behavior; for example Ctrl+C during `ui record` still finalizes
the recording and returns its normal successful result.
