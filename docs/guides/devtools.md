# Inspect a WinUI app with DevTools

DevTools lets you inspect a running WinUI 3 app built with the Windows App SDK,
try live dependency-property changes, diagnose bindings, and leave source-anchored
comments for an agent to turn into code changes.

```powershell
winapp run . --devtools
```

This builds and launches your app with the in-app overlay open. Pick an element to
inspect it or leave a review comment. The command waits for the app; cancelling it
stops the process it launched.

For a short terminal workflow, detach once and reuse the returned PID:

```powershell
$run = winapp run . --devtools --detach --json | ConvertFrom-Json
winapp devtools inspect -a $run.ProcessId
winapp devtools comments list
```

For scripts, launch JSON uses `ProcessId`; DevTools JSON uses `pid`. Pass the
launch PID to `-a` when more than one app is attached.

The quick peek opens with a **Comment** box, followed by up to five property values.
Expand a value to edit it, or expand its XAML file-and-line header to read the
source. **Open in DevTools** opens the full inspector for detailed binding work.
Hover picking pauses while the quick peek or comment editor is open, preserving
your selection and draft.

Picking selects the element you declared: clicking a control's template part, such as
a TextBox's placeholder, selects the control from your XAML. An element whose source
is unknown is selected as-is rather than redirected. To pick framework and template
parts themselves, turn off **Just my XAML** in the inspector window.

Comment markers follow elements on the active window's XamlRoot. Comments that
cannot resolve there remain saved but unplaced. Markers disappear while their
element or an ancestor is collapsed, unloaded, or fully transparent, and return
when the element is shown again. The Comments badge counts all saved open comments,
not just markers currently visible.

Use a project, solution, build-output folder, or .NET file-based app as the `run`
input. A DevTools launch prepares source diagnostics and, for managed apps, startup
binding support before the app starts. It preserves unrelated startup hooks and
developer environment settings.

For a project configured for Native AOT, use:

```powershell
winapp run . --aot --devtools
```

Native inspection and comments remain available. Managed binding diagnosis and
binding source writes require the CLR and are unavailable in Native AOT apps.

By default, DevTools inspects apps on this machine. To inspect inside Windows
Sandbox, use the [Sandbox workflow](#inspect-inside-windows-sandbox). Never pass a
guest PID to local `devtools attach` or inspection commands.

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

## Refresh inspector values

In the inspector window, select **Refresh snapshot** or press **F5** to reread
property values and tree previews after your app changes them. Values are
snapshots, not live subscriptions. Selecting the same element again does not refresh
them. Finish or cancel pending edits before refreshing.

## Continue from a terminal

Use a second terminal for inspection commands while the app runs:

```powershell
winapp devtools inspect
```

Alternatively, `winapp run . --devtools --detach` prints the PID and returns to
your terminal. Use that PID with `-a` when more than one app is attached.

Add `--json` only when a script or structured consumer needs JSON output. It
changes output formatting, not the overlay. For example:

```powershell
winapp run . --devtools --detach --json --no-overlay
```

`--no-overlay` explicitly suppresses the in-app overlay and requires `--devtools`.
Both human-readable and JSON launches open the overlay unless you opt out.

## Review comments with an agent

Leave comments on elements through the running app's overlay. Then ask your agent
to read the comments and their current source matches:

```powershell
winapp devtools comments list
winapp devtools comments list --status open --json
```

Human `list` output shows open comments; use `list --all` to include resolved,
stale, and dismissed comments. `list --json` includes all statuses by default.
Saved `list`, `get`, `update`, and `delete` operations use the host's local store;
they do not require the app or Sandbox to be running. Choose the host project with
`--source-root`; use `list --project` to filter a shared repository store. `list`
and `get` do not accept `--app` or `--on`.

`get` shows the captured declaration and historical creation location separately
from current source matches. Runtime text is context, not a required XAML literal.
A unique declaration or identifier in the captured project, file, and ancestor
scope can remain confirmed after line moves. Repeated templates, moved files, and
ambiguous matches remain ranked candidates; confirm the intended candidate before
editing.

The agent should verify each source location, make the requested change, and
resolve the comment only after checking the result:

```powershell
winapp devtools comments get cmt_example
winapp devtools comments update cmt_example --status resolved --note "Updated the label"
```

Replace `cmt_example` with an actual ID from `list`. If the intended element
cannot be confirmed, use `comments update <id> --status stale --note "Element moved"`
rather than guessing. Listing comments does not edit source automatically.

`update --status` accepts `open`, `resolved`, `stale`, or `dismissed`. Every
add/update/delete refreshes the markers of your running DevTools apps whose project
uses the same store, including a Sandbox app launched with `winapp run --devtools`
(within about a second). `--app <pid>` only names an extra target to refresh.

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

## Find and inspect an element

```powershell
winapp devtools inspect -a 12345
winapp devtools search Save -a 12345
winapp devtools get-property SaveButton -p Content -a 12345
winapp devtools get-layout SaveButton -a 12345
winapp devtools get-source SaveButton -a 12345
winapp devtools diagnose-binding SaveButton Content -a 12345
```

Use the selector printed in brackets, a unique `x:Name`, or an exact element
handle. Ambiguous names fail rather than selecting the first match. Handles belong
to the current live tree: inspect again after replacing an element or restarting
the app.

`winapp devtools get-source <element> --json` preserves the runtime's file, line,
and column. When compiler artifacts let DevTools verify a unique authored XAML
declaration, JSON adds authored coordinates and provenance. New comments captured
from that element use the authored declaration; existing notes are not rewritten.

**Disk-matched source does not prove what the running app loaded.** After a rebuild
or hot reload, the app may retain different XAML. Check the displayed declaration
before editing or anchoring a comment.

When compiler artifacts are missing, DevTools can display **likely source**. This
is weaker than a verified match and requires confirmation before saving a comment:

```powershell
winapp devtools get-source <element> -a 12345
winapp devtools comments add --from-element <element> --app 12345 --text "Review this layout" --confirm-likely-source
```

Without confirmation, nothing is saved for a likely capture. The in-app comment
editors show an unchecked **Use this likely source for my comment** box.

Inspection and search prefer your app's authored XAML; `--all` includes framework
and control-template elements. `inspect --depth 8` expands more levels. `search Save`
matches text content as well as type, `x:Name`, and source file. Text comes from
realized TextBlock/TextBox elements and primitive Content values, not from executing
bindings or converters. Uncreated virtualized items are not searched.

### Filter and read several elements

```powershell
winapp devtools search --of-type TextBlock --with 'FontSize>=20' --fields 'Text,FontSize' -a 12345
winapp devtools inspect PlannerSurface --of-type TextBlock --with 'FontSize>=20' --with 'Visibility==Visible' -a 12345
winapp devtools search --of-type TextBox --fields 'Text,PlaceholderText,AutomationProperties.Name' -a 12345
winapp devtools search 'Today' --fields 'Text,FontSize' -a 12345
```

`--of-type` matches an exact runtime XAML type, not subclasses. Repeat `--with`
for AND. Supported operators are `==`, `!=`, `<`, `<=`, `>`, `>=`, and `*=` for
ordinal, case-sensitive substrings. Quote the whole predicate. Property names must
be exposed by the runtime; attached property names are allowed, but nested object
paths and application getters are not.

`inspect` retains contextual ancestors and labels them `(context)`; `search`
returns a flat list. Without `--fields`, compact previews label their values.
`<n/a>`, `null`, `""`, `<unset>`, `<unresolved>`, and `<unreadable>` are distinct.
Use `--fields` for full requested values; it does not filter matches.

For AND predicates, a definite typed mismatch excludes an element even when another
predicate value is missing or unreadable. Without a definite mismatch, unknown
predicate values leave the element unevaluated. Ambiguous property metadata and
failed reads still block exact targeting. Depth, node, and evaluation budgets still
apply; a partial result never proves a unique target. A zero-match search exits
nonzero, with incomplete coverage distinguished from a complete search.

### Read or change one query-selected element

```powershell
winapp devtools get-property --of-type TextBlock --with 'Text==Today' -p FontSize -a 12345
winapp devtools set-property --of-type TextBlock --with 'Text==Today' -p Text --value 'Tomorrow' -a 12345
```

Query mode omits the positional selector; `set-property` requires `--value`.
Do not combine a positional selector with query criteria. Both commands require
exactly one provable match in the app's live XAML census. Zero, multiple, uncertain,
or truncated matches refuse the operation.

For uncertain candidates, failure output includes current-session handle selectors,
census identity, and each predicate's true/false/unknown result. Use the reasons to
refine your query; missing details never make a refused write safe to retry
automatically.

A query-targeted write rechecks the eligible scope and target in one app-side
dispatcher operation, then reports observed before and after values. It does not
edit source or guarantee that a binding remains healthy. If a dispatched write times
out, its outcome is **indeterminate**. Read the target's property to reconcile; do
not automatically retry. These options apply to XAML DevTools, not `winapp ui`.

### Diagnose bindings

Binding diagnosis is a point-in-time path/type evaluation, not a test of freshness
or change notifications. Add `--json` to `diagnose-binding` to see all observations.
Different `sourceValue`, `resolvedValue`, and `targetValue` values do not prove a
fault, especially with OneTime bindings or converters. Diagnosis can execute source
getters and runtime converters. It does not call `ConvertBack` or source setters.

Without authored source information, no runtime binding does **not** prove a
property is unbound. Use a build with XAML source information to inspect otherwise
invisible compiled bindings.

For a native path read, use:

```powershell
winapp devtools call Binding.walk handle=123 prop=Text -a 12345
```

This does not require a .NET host or startup hook. It checks path getters, not
converters, source notifications, or reverse propagation. `unknown`,
`path-unavailable`, and `not-probeable` are walker limitations, not proof that a
binding is broken.

`-a` accepts a PID, process name, or window title; `-w` accepts a window handle.
Without either, the command requires exactly one attached app. Live commands never
inject just because a target was selected. To authorize injection, use
`winapp devtools attach --pid <pid>` first, or add `--attach` to the live command.

### Target a window or subtree

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

## Try a live property change

```powershell
winapp devtools set-property SaveButton 200 -p Width -a 12345
```

The command reads the value before and after the write and reports what actually
took effect. Changes are in-memory; they do not edit your source files and disappear
when the app restarts. In JSON, `valueSource` is the runtime precedence slot,
`binding` is a remaining runtime expression, and `authored` is the original XAML
when available.

In the inspector's Binding editor, path and mode edits are **full replacements**,
not edits to the existing Binding object. Review the warning and click
**Replace entire binding**. Compiled x:Bind remains read-only; switching to a
classic Binding does not edit generated code.

If a literal cannot be converted or is outside the property's allowed range, the
error names the property, requested value, and XAML type. Correct that input and
retry; for example, Width must be nonnegative, not `banana` or `-1`.

For a managed binding, capture it before temporarily replacing its value:

```powershell
winapp devtools call Binding.capture handle=123 prop=Content -a 12345
winapp devtools set-property 123 "Temporary label" -p Content -a 12345
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

To clear a local property override, use `Binding.clearValue` for a managed target
or `HotReload.clearProperty` for a native dependency property, with the same
`handle` and `prop` parameters. Captures are process-local, not durable backups.

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

## Comment storage and advanced authoring

The usual workflow starts with comments a person leaves in the overlay. For
explicit programmatic authoring, `comments add` is also available:

```powershell
winapp devtools comments add --app 12345 --from-element SaveButton --text "Make this label clearer"
```

You can also pick an element in the overlay and use `--from-selection`, or author a
comment without a running app using `--file`, `--name`, and `--text`. `add --id <id>`
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

In the inline comment editor, choose **Save**, press **Ctrl+Enter**, or leave the
field to save. **Saving...** is not confirmation: wait for **Saved**. If saving
fails, the draft stays available and the status shows the failing stage and code.
Copy the text before closing an unconfirmed draft.

Comments preserve multiline text, Unicode, and whitespace. A live marker requires
matching source and parent context: a same-named control on another page or dialog
does not inherit the note. If a note cannot be placed, select its intended element
and recapture it with `comments add --id <id> --from-selection`.

A failed marker refresh does not undo a saved comment; the command reports a warning
and does not claim it placed a marker. Reattach to retry. A corrupt store is left intact; fix or recover
it before retrying a write.

## When a command fails

Commands return nonzero for invalid targets, refused operations, or unusable
responses. Live-command JSON includes `ok`, `pid`, and an `error` object; `attach`
and comment commands use a string `error`. Check exit status and `ok`, not just
whether JSON was printed.

If `run --on sandbox --devtools` fails before readiness, it reports the startup
failure and host-side launch diagnostics path. A negotiation error means the
inspector did not return usable capabilities; a host-comment binding refusal means
its reported authority could not be verified. Keep the error and diagnostics when
reporting a failure, and do not reuse selectors from an earlier run.

If attachment reports a transport-limit error, shorten the portable CLI and
application paths before retrying. If it persists, report the error without
modifying target connection files or comment-binding data.

For a portable installation, keep matching `WinApp.DevTools.Native.dll` and
`WinApp.DevTools.Managed.dll` beside `winapp.exe`. If either is missing, reinstall
the complete package rather than mixing DLLs from different builds. PDB files and
the standalone protocol schema are not required at runtime. When upgrading a
private prerelease, close inspected apps and restart them with the new CLI.

For packaged apps, winapp automatically prepares the selected application's
execution alias in the staged manifest. You do not need to author one manually. If
a packaged alias is disabled or its target cannot be verified, launch fails. Enable
its entry in Windows App execution aliases and retry. `--devtools` cannot be
combined with `--no-launch` or `--without-alias`; the [other run-option restrictions](../usage.md#run)
still apply.

Use `winapp run --devtools` directly for this workflow. The NuGet `dotnet run`
integration forwards a `false` alias preference as `--without-alias`, so adding
`--devtools` through `WinAppRunArgs` can conflict with that forwarded option.

If the newly launched process exits or redirects to an existing instance before
DevTools connects, `run` reports failure. It does not adopt another instance. Close
that instance and relaunch, or attach to its exact PID with the
[late-attachment limitations](#attach-to-an-app-that-is-already-running).

If attachment cannot find the target's Windows App Runtime, ensure the target is a
running WinUI 3 app. For an explicit runtime override, set
`WINAPP_DEVTOOLS_FRAMEWORKUDK` to that target runtime's
`Microsoft.Internal.FrameworkUdk.dll`. The path must be local and must not traverse
a symlink or junction. Repair the runtime or remove the override rather than
substituting another DLL.

If the overlay or inspector reports a missing XAML resource, ensure your app merges
`XamlControlsResources` into `Application.Resources`, as standard WinUI templates
do. Resource-less hosts can run without an overlay (`--no-overlay`), but the
overlay and inspector require those resources.

A packaged CLI stages foreign-load engines in `%USERPROFILE%\.winapp\engine`,
outside AppData. This location intentionally does not follow
`WINAPP_CLI_CACHE_DIRECTORY`. If staging is refused, check the named path's
ownership and write permissions before retrying.

For access-denied errors, run the CLI as the app's owner at the same or higher
integrity level. Do not disable pipe security to make attachment work.
