# Advanced DevTools

This page is the reference behind [Inspect a WinUI app with DevTools](devtools.md):
Windows Sandbox, attaching to a running app, targeting, query rules, comment storage,
source locations, JSON output, the protocol and troubleshooting.

## Inspect inside Windows Sandbox

```powershell
$run = winapp run . --on sandbox --devtools on --detach --json | ConvertFrom-Json
winapp devtools inspect --on sandbox --app $run.appSelector
winapp devtools comments list --status open --json
```

Complete the [Windows Sandbox prerequisites](../sandbox-execution.md#before-you-start)
first. The project builds on your machine; the app, toolbar and inspector run in
Sandbox. DevTools is off by default there, so pass `--devtools on` (or `headless`). It
needs a project with XAML sources; build-output folders and .NET file-based apps aren't
supported. Host and guest need matching winapp and DevTools engines.

Use `--on sandbox --app <pid>` with a PID from `winapp devtools list --on sandbox`, or
the returned `appSelector`. Discovery doesn't start or repair Sandbox: after it is
recreated or the app exits, relaunch. Never pass a guest PID to a local command.

Only your project's XAML files are copied into a read-only snapshot in the guest; rebuild
and relaunch after changing source. Comments written in the guest are saved in the host
project's store, and the UI reports success only after the host confirms. They survive
Sandbox, so read them on the host without `--on sandbox`:

```powershell
winapp devtools comments list --source-root C:\src\MyApp
```

To attach to an app already running in the guest, use `winapp devtools list --on
sandbox --include-available`, then `winapp devtools attach --on sandbox --app <pid>`.
Late attachment has the limits below and no host comment mapping.

## Attach to an app that is already running

```powershell
winapp devtools list --include-available
winapp devtools attach --pid 12345
winapp devtools inspect -a 12345 --all
```

`list` doesn't inject anything. `attach` loads DevTools into the process without
restarting it, headless unless you add `--overlay` (toolbar) or `--show-window`
(inspector). It stays until the app exits. Other commands never inject on their own:
attach first, or add `--attach`. `-a` takes a PID, process name or window title; `-w`
takes a window handle.

Late attachment can't add the .NET startup hook or source information recorded at
launch, so binding operations and source locations need an app started by `winapp run`.
Binding diagnosis needs .NET: in Native AOT (`winapp run . --aot`) and C++ apps it
reports `unavailable`. Connections are local-only.

### Visual UI prerequisites

The toolbar and inspector use the app's WinUI XAML metadata and theme resources. If the
app doesn't merge `XamlControlsResources` into its application resources (standard WinUI
templates do), the overlay fails with the runtime's error, such as a missing
`AcrylicBackgroundFillColorDefaultBrush`, and the app stays inspectable headless:

```powershell
winapp run . --devtools headless
```

For a custom C++/WinRT startup, delegate `IXamlMetadataProvider` to
`Microsoft.UI.Xaml.XamlTypeInfo.XamlControlsXamlMetaDataProvider` and merge
`XamlControlsResources`.

## Target a window or subtree

```powershell
winapp devtools call Surface.list -a 12345
winapp devtools inspect --window 657922 -a 12345
winapp devtools search --root 9001 --of-type TextBlock --fields Text -a 12345
```

`--window` takes a `window` value from `Surface.list`; `--root` takes an element handle
from `inspect`. Both constrain searches, reads and writes; a closed window or a handle
from another window fails instead of falling back to the whole process. Handles belong
to the current live tree: inspect again after the app replaces an element or restarts.

## Query targeting

`--of-type` matches an exact runtime type. Repeat `--with` for AND; quote each
predicate. `search` matches text from realized elements, not bindings; when nothing in
your XAML matches, it searches the whole tree (for example, items created from data)
and says so. A search with no match exits nonzero.

`get-property` and `set-property` with `--of-type`/`--with` instead of a selector need
exactly one provable match. Zero, several, unreadable or truncated matches refuse the
operation and list each candidate's predicate results. A query write rechecks its
target in the app before writing. If it times out, the outcome is indeterminate: read
the property to check, and don't retry automatically.

## Source locations

`get-source` prints the element's declaration, such as `FocusPanel.xaml:24-26`, then the
tag. If the file changed since the build, or the declaration can't be confirmed, it
says so instead. When compiler output is missing, DevTools may show a **likely source**.
A comment on it is saved only after you confirm it:

```powershell
winapp devtools comments add --from-element <element> --app 12345 --text "Review this" --confirm-likely-source
```

`diagnose-binding` evaluates path and types at that moment, not change notifications. It
can run source getters and converters, never `ConvertBack` or setters. Different source
and target values don't prove a fault, and without source information, finding no
binding doesn't prove a property is unbound.

## Comment storage and advanced authoring

Comments are saved in `.winapp/ui-comments.json` at the repository root, or in the
source directory when there is no repository. An app launched without a project, such as
a C++ app from its build output, keeps them in the folder you ran `winapp run` from; it
shows them in its list and count but can't place markers.

```powershell
winapp devtools comments add -a 12345 --from-element SaveButton --text "Make this label clearer"
```

- `--from-selection` comments on the element picked in the toolbar.
- `--file`, `--name` and `--text` work without a running app; a `--name` that matches
  one element in the file anchors to it.
- `--id <id>` replaces that comment; `delete <id>` removes it.
- `--source-root <dir>` picks the project; `list --project` filters a shared store;
  `list -a <pid>` uses that app's project. Comment reads never take `--on`.

A comment captured from a live element also records its AutomationId
(`anchor.identity.automationId`), window title (`context.windowTitle`), style
(`context.style`) and brushes (`context.brushes`: value, where it comes from, and the
resource key and file when your project set it).

Changes refresh markers in running DevTools apps for the same project, Sandbox included;
`update` and `delete` take `--app <pid>` for another app. A failed marker refresh warns
but doesn't undo the save. A corrupt store is left untouched. In the app, **Saving...**
isn't confirmation; the **Comment saved** notice is, and a failed save keeps your draft.

## JSON output

`winapp run --json`'s `devTools` fields are listed under [devtools](../usage.md#devtools) in the
command reference; `source` is `explicit`, `setting`, `default`, `ci`, `option` or `not-winui`.

Live commands return `ok`, `processId` and an `error` object; `attach` and comment
commands return a string `error`. Check the exit code and `ok`. Elements carry `file`,
and when confirmed `line`, `endLine` and `column`. `set-property` reports `valueSource`,
`binding`, `authored`, `chain` (every competing value, `winner` marking the effective
one) and `replacedBinding`. Password values add `"redacted": true`.

## Use the DevTools protocol

`call` runs methods advertised by `DevTools.negotiate`. `name=value` passes a string;
`name:=value` passes JSON:

```powershell
winapp devtools call DevTools.negotiate -a 12345
winapp devtools call VisualTree.enumerate depth:=4 -a 12345
```

`call` doesn't resolve selectors: pass the numeric `handle` string from `inspect --json`
as `handle=123`. The protocol is experimental (`protocolVersion` `"0"`); methods may
change.

### Read a binding path natively

```powershell
winapp devtools call Binding.walk handle=123 prop=Text -a 12345
```

This needs no .NET hook. It checks path getters only; `unknown`, `path-unavailable` and
`not-probeable` are limits of the walker, not a broken binding.

### Capture and restore a binding

```powershell
winapp devtools call Binding.capture handle=123 prop=Content -a 12345
winapp devtools set-property 123 Content "Temporary label" -a 12345
winapp devtools call Binding.restore handle=123 prop=Content -a 12345
```

Restoring an `{x:Bind}` re-applies every compiled binding on its owner, so it asks for
`confirmOwnerRebind:=true`. `writesThrough` in the capture says whether edits reach the
source; restore re-reads the source and is not an undo. The inspector offers **Restore
binding** on a property you edited. `Binding.clearValue` (managed) and
`HotReload.clearProperty` (native) clear a local value.

## Troubleshooting

- **Missing engine:** keep `WinApp.DevTools.Native.dll` and `WinApp.DevTools.Managed.dll`
  beside `winapp.exe`; reinstall rather than mixing builds.
- **Execution alias:** packaged apps need their App execution alias enabled. `--devtools
  on` can't be combined with `--no-launch` or `--without-alias`; with `dotnet run`,
  `WinAppRunNoLaunch=true` or `WinAppRunUseExecutionAlias=false` turns DevTools off.
- **App can't be closed:** `run` names its PID; close it, or attach to that PID.
- **Windows App Runtime not found:** check the target is a running WinUI 3 app, or set
  `WINAPP_DEVTOOLS_FRAMEWORKUDK` to its `Microsoft.Internal.FrameworkUdk.dll` (local, no
  links).
- **Staging refused:** a packaged winapp stages the engine in `%USERPROFILE%\.winapp\engine`;
  check that path isn't redirected by a link and isn't held by an app.
- **Access denied:** run winapp as the app's user at the same or higher integrity.
- **Transport limit:** the engine path beside `winapp.exe` must be under 260 characters;
  install winapp at a shorter path.
- **Sandbox launch failed:** keep the reported diagnostics path; don't reuse selectors
  from an earlier run. Set `WINAPP_DEVTOOLS_LOG=1` before launch for crash diagnostics
  (dumps may contain app memory).