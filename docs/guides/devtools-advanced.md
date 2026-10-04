# Advanced DevTools

This page covers DevTools beyond the everyday workflow in
[Inspect a WinUI app with DevTools](devtools.md): Windows Sandbox, attaching to an app
that is already running, targeting one window or subtree, comment storage, the DevTools protocol, and what
DevTools costs a running app.

## Inspect inside Windows Sandbox

From your WinUI project directory:

```powershell
$run = winapp run . --on sandbox --devtools --detach --json | ConvertFrom-Json
winapp devtools inspect --on sandbox --app $run.appSelector
winapp devtools comments list --status open --json
```

Complete the [Windows Sandbox prerequisites](../sandbox-execution.md#before-you-start)
first. The project builds on your machine; the app, overlay, and inspector run
inside Sandbox. Add `--no-overlay` to suppress the guest overlay, or omit
`--detach` to wait for the app and stop the owned launch when you cancel.

Guest DevTools requests a normal app close before forced cleanup. Save your app's
work before cancelling the session.

For interactive commands, use `--on sandbox --app <pid>` with a PID from
`winapp devtools list --on sandbox`. Process names and window titles also work
when unambiguous. Scripts can keep using the returned `appSelector`, which
identifies that launch. Saved comment reads do not need a live target.

Discovery and inspection do not start or repair Sandbox. If the guest disconnects,
the app exits, or Sandbox is recreated, rediscover or relaunch. Stale launch
selectors and element handles are rejected. There is no fallback to an app on your
desktop.

A Sandbox DevTools launch requires a project with evaluated XAML source files.
Build-output-only inputs and .NET file-based apps are not supported. Keep the
packaged app's Windows execution alias enabled. The host and guest must have
matching winapp and DevTools engines; a mismatch fails with guidance.

Only admitted project XAML files are copied into a read-only guest source
snapshot. This does not share your project or comment store for writing. Rebuild
and relaunch after changing source; a running app keeps its launch snapshot.
Guest inspection and source capture cannot override the mapping with `--source-root`,
`--project`, or a window handle.

**Comments belong to the host project.** Comments written in the guest overlay,
inspector, or scoped CLI are saved in the same durable host store as local
comments. The UI reports success only after host acknowledgement. A failed save
keeps the draft; a marker-refresh warning is distinct from a failed save. Do not
automatically replay uncertain mutations after disconnecting.

Saved comments survive app exit and Sandbox recreation. Once the app is gone, read
them on the host without `--on sandbox`:

```powershell
winapp devtools comments list --source-root C:\src\MyApp
winapp devtools comments list --status open --json --source-root C:\src\MyApp
```

For an app already running in the guest, discover its PID first:

```powershell
winapp devtools list --on sandbox --include-available
winapp devtools attach --on sandbox --app 12345
```

Use the displayed PID in place of `12345`; `sandbox:<pid>` is not a selector. Late
attachment cannot add startup-only binding support, recover missing source
information, or establish a persistent host comment mapping. Use the project launch
above when you need those capabilities.

## Attach to an app that is already running

DevTools connections are local-only; remote named-pipe clients are rejected. Crash
diagnostics are off by default. To opt in, set `WINAPP_DEVTOOLS_LOG=1` in the
app's environment before launch. Dumps may contain app memory; keep them private
and delete them after diagnosis.

```powershell
winapp devtools list --include-available
winapp devtools attach --pid 12345
winapp devtools inspect -a 12345 --all
```

`list` does not inject anything. `attach` loads the inspection agent into the named
process without restarting it; repeated attachment reuses the agent. Attachment is
headless by default. Add `--overlay` for the in-app toolbar or `--show-window` for
the inspector window. The agent remains until the app exits.

Late attachment cannot add a .NET startup hook or recover source information that
was not recorded at launch. Native tree/property inspection is still available,
but managed binding operations need an app launched with the hook. Source reporting
also depends on the app providing XAML source information. Use `winapp run --devtools`
when you need startup instrumentation.

### Visual UI prerequisites

```powershell
winapp run . --devtools --no-overlay
winapp devtools inspect -a 12345
```

Use headless inspection when the app cannot load the DevTools visual UI. The toolbar
and inspector use the app's own WinUI XAML metadata and theme resources. A
programmatic app can support tree/property inspection while lacking those visual
prerequisites.

If a requested overlay fails, the command returns nonzero and includes the XAML
runtime diagnostic, such as a missing `AcrylicBackgroundFillColorDefaultBrush`.
For a local app, the process remains attached and can still be inspected by PID. A
failed Sandbox startup stops its owned guest app; relaunch with `--no-overlay` for
headless inspection.

For a custom C++/WinRT startup, delegate the application's `IXamlMetadataProvider`
to `Microsoft.UI.Xaml.XamlTypeInfo.XamlControlsXamlMetaDataProvider` and merge
`Microsoft.UI.Xaml.Controls.XamlControlsResources` into the application resources,
as standard WinUI templates do. DevTools does not add these resources or replace
the application's metadata provider. Keep the reported error if the app already
supplies both.

## Target a window or subtree

```powershell
winapp devtools call Surface.list -a 12345
winapp devtools inspect --window 657922 -a 12345
winapp devtools search --root 9001 --of-type TextBlock --fields Text -a 12345
```

Use the live `window` value returned by `Surface.list` for `--window`. For `--root`,
copy an element handle from window-scoped `inspect`. `Surface.list`'s `rootHandle`
can identify a popup subtree rather than the app's content. Do not substitute
`winapp ui` selectors. `--root` constrains searches, reads, and writes to that
element and its descendants; it does not change process-wide identities. When both
options are supplied, the root must belong to the selected window. Refresh
inspection after a rebuild or closed window rather than reusing stale handles.

`--window <HWND>` constrains tree searches, element reads, and property writes to
that window's live XAML elements. Cross-window handles, closed windows, and
unverifiable mappings fail rather than falling back to the whole process. Advanced
protocol operations without scope support fail explicitly when a scope is supplied.
Attach and app discovery remain process-scoped.

Interactive `Selection.arm` supports window scope, not `--root` subtree scope. It
refuses a subtree constraint rather than allowing subsequent picks outside it.

## Comment storage and advanced authoring

The usual workflow starts with comments a person leaves in the overlay. For
explicit programmatic authoring, `comments add` is also available:

```powershell
winapp devtools comments add -a 12345 --from-element SaveButton --text "Make this label clearer"
```

You can also pick an element in the overlay and use `--from-selection`, or author a
comment without a running app using `--file`, `--name`, and `--text`. When `--name`
matches exactly one element in `--file`, the comment is anchored to that declaration
as if it had been captured live. `add --id <id>`
updates an existing comment. `comments update <id> --status stale --note "Element moved"`
records an uncertain anchor; `delete <id>` permanently removes the comment.

Comments persist in `.winapp/ui-comments.json` at the enclosing repository root.
Without a repository, the supplied source directory is used. Live capture uses the
app's reported source root; `--source-root <dir>` overrides it and also locates the
store for later commands.

Live markers include only comments from the app's reported project or explicit
`--source-root`. Unassigned comments remain in the store but are not sent to a
running app. If `--from-element` reports an incomplete name search, pass an exact
handle or identity selector from `inspect` instead.

In the inline comment editor, press **Enter**, choose **Save**, or leave the field to
save; **Shift+Enter** adds a line. Clicking another element or pressing Esc also saves
what you typed. **Saving...** is not confirmation: a **Comment saved** notice by the
toolbar confirms each save, including saves that finish after the panel moved on.
After **Enter** or **Save**, the panel closes once the comment is saved and linked to
source; otherwise it stays open with the status. If saving
fails, the draft stays available and the status shows the failing stage and code.
Copy the text before closing an unconfirmed draft.

Comments preserve multiline text, Unicode, and whitespace. A live marker requires
matching source and parent context: a same-named control on another page or dialog
does not inherit the note. If a note cannot be placed, select its intended element
and recapture it with `comments add --id <id> --from-selection`.

A failed marker refresh does not undo a saved comment; the command reports a warning
and does not claim it placed a marker. Reattach to retry. A corrupt store is left intact; fix or recover
it before retrying a write.

## Use the DevTools protocol

`call` exposes methods advertised by `DevTools.negotiate`; it rejects internal and
unknown methods. Parameters use `name=value` for strings and `name:=value` for JSON
numbers, booleans, arrays, objects, or null:

```powershell
winapp devtools call DevTools.negotiate -a 12345
winapp devtools call VisualTree.enumerate depth:=4 -a 12345
```

Raw `call` does not resolve element names or selectors. Copy the element's numeric
`handle` string from `inspect --json` or `search --json`, then pass it unchanged as
`handle=123`. Do not pass the `selector` field or use `handle:=123`: handles are
opaque decimal strings.

`DevTools.negotiate` lists the methods the attached app supports. While the protocol
is experimental (`"experimental": true`), its `protocolVersion` is `"0"` and methods
may change between releases.

`Overlay.getState` reports where the in-app chrome is hosted in `host`: `uiLayer`
(the XAML diagnostics layer above the app) or `popup`, the fallback used when that
layer is unavailable, which places the chrome in the app's outermost panel and under
open dialogs.

### Read a binding path natively

```powershell
winapp devtools call Binding.walk handle=123 prop=Text -a 12345
```

This does not require a .NET host or startup hook. It checks path getters, not
converters, source notifications, or reverse propagation. `unknown`,
`path-unavailable`, and `not-probeable` are walker limitations, not proof that a
binding is broken.

### Capture and restore a binding

For a managed binding, capture it before temporarily replacing its value:

```powershell
winapp devtools call Binding.capture handle=123 prop=Content -a 12345
winapp devtools set-property 123 Content "Temporary label" -a 12345
winapp devtools call Binding.restore handle=123 prop=Content -a 12345
```

Classic binding restore affects only that element/property. Compiled x:Bind restore
can refresh all compiled binding targets on the owning object. If restore reports
`confirmation-required`, review its owner/scope warning, then confirm only when
that broader refresh is intended:

```powershell
winapp devtools call Binding.restore handle=123 prop=Content confirmOwnerRebind:=true -a 12345
```

Before editing a bound value, inspect `Binding.capture`. `writesThrough` reports
whether a source write is known, false, or unknown. For TwoWay TextBox.Text, a
PropertyChanged trigger can update the view model immediately. **Restore binding is
not model undo**: it re-reads the current source, including any edits already
written there.

In the inspector, a bound property you edited live shows **Replaced by a local
value** (`{Binding}`) or **Overridden by a live edit** (`{x:Bind}`) with a
**Restore binding** button. For `{x:Bind}`, restore re-applies every x:Bind on the
page.

To clear a local property override, use `Binding.clearValue` for a managed target
or `HotReload.clearProperty` for a native dependency property, with the same
`handle` and `prop` parameters. Captures are process-local, not durable backups.

## Performance

Measured on a small WinUI sample app:

- A visible, idle overlay does no work on the app's UI thread.
- Highlighting an element takes about 1.6 ms on the UI thread.
- Opening the inspector window stalls the UI thread once, for about 0.2 s, and adds about 20 MB of private memory while it is open.
- The overlay itself adds 2–3 MB of private memory.
- Attaching to an app that is already running takes 0.7–1.1 s.
- While DevTools is attached, memory grows by about 130 bytes for every XAML element the app creates, and that memory is not
  released while the app runs. In an app that keeps rebuilding its pages, such as navigating between heavy pages, that is a few
  MB per cycle. This memory is held by WinUI's diagnostics while DevTools is subscribed to the visual tree, not by DevTools'
  own element index, which shrinks as elements leave the tree. It is the same with `--no-overlay`. Restart the app without
  `--devtools` when you measure memory.
