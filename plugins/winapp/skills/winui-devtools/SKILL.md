---
name: winui-devtools
description: "Inspect a running WinUI 3 app's XAML or Reactor UI with winapp devtools: property values, bindings, the XAML line or C# call site behind an element, Reactor components, state, handlers and logs, live edits, and UI review comments. To click, type, read text or screenshot, use winapp-ui-automation."
---

**If you can't run `winapp` yourself** (no shell, or the command is denied), give the user the exact `winapp devtools` / `winapp run` command(s) that would answer their question instead of only reasoning from source. Say which values you read from source and which only the running app can confirm.

## Start with the target

Use `winapp ui` and the `winapp-ui-automation` skill for general Windows UI
Automation: actions, screenshots, waits and accessibility across app frameworks.
DevTools is for WinUI 3 XAML trees/source, bindings and dependency-property
diagnosis or edits. Both tools accept an element's AutomationId as its selector,
so an AutomationId from `winapp ui` works in `winapp devtools` and the `aid=`
shown by DevTools works in `winapp ui invoke/click`. Generated selectors (slugs,
handles) belong to one tool; copy a fresh one from that tool's `inspect` or
`search`.

```powershell
winapp run .
```

For a WinUI project this starts DevTools with its in-app toolbar and waits for the app
(it is off in CI, with `--no-launch`, `--without-alias` or `--on sandbox`, and for
folders, .cs files and non-WinUI projects; pass `--devtools on` there). Let the user
leave comments there:
turn on **Comments** in the toolbar, click an element, type, press Enter (Shift+Enter for a
new line), or click the next element to save and move on. **Select element** opens the
DevTools window on the element the user clicks, then turns picking off so the next
click reaches the app. Ctrl+Shift+F12 moves keyboard
focus to the toolbar (showing it if the user hid it), and Esc returns it. Use a second terminal for commands, or add `--detach`
to return to the terminal with the app still running and its PID printed.
Use an exact PID with `-a` when multiple apps are attached.
Never stop another app automatically to make a rebuild succeed.
For an edit loop on a .NET 8+ project (packaged or unpackaged), `winapp run . --watch`
runs it under `dotnet watch`: Hot Reload edits keep DevTools attached, and when an
edit needs a restart the app is relaunched (a packaged app is registered again first)
and DevTools attaches to the new process (it prints the new PID).
Re-read handles after a restart. `dotnet watch` ignores `.xaml` edits until a C# file
changes or the user presses Ctrl+R. App arguments go after `--` (for example Reactor's
`-- --preview <Component>`).

`--json` is optional output formatting for structured consumers, not the default
agent workflow. It does not hide the overlay. Use `--devtools headless` for an
authorized headless inspection launch: nothing is drawn in the app, and
Ctrl+Shift+F12 brings up the toolbar. `--devtools off` runs without DevTools; don't
change the user's default (`winapp config set run.devtools`) unless they ask.
For a script, capture `$run = winapp run . --devtools on --detach --json | ConvertFrom-Json`,
then use `winapp devtools inspect -a $run.ProcessId`. After the human leaves a comment, follow
[the comment workflow below](#turn-the-users-comments-into-changes).
If attachment succeeds but the overlay reports a missing XAML resource/type,
see [visual UI prerequisites](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/devtools-advanced.md#visual-ui-prerequisites);
do not mistake unavailable visual UI for failed protocol attachment.

Packaged aliases are prepared automatically in the staged manifest, not manually
authored in the source manifest. When DevTools starts by default and how to choose,
including `dotnet run`'s `-p:WinAppRunDevTools`, is in
[Turn DevTools on or off](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/devtools.md#turn-devtools-on-or-off).

Do not adopt a different running instance when launch fails. Attaching to an
existing app is explicit and headless by default:

```powershell
winapp devtools list --include-available
winapp devtools attach --pid <pid> --source-root <project folder>
```

`--source-root` gives the app its project folder, so in-app comments go to the same store
as with `winapp run`; an app started without `winapp run` has none. `list` and `attach`
only take WinUI 3 apps.

Attach also loads the managed agent into a running .NET 8+ app (binding diagnosis,
Reactor tree/state); read `managedAgent` in `--json` output. Late attach cannot
recover XAML source locations or Reactor C# call sites; those need `winapp run` with DevTools. Obtain permission before launching, injecting into, or mutating
an app; do not use an unrelated running app as a test fixture.
Live commands require an attached target. Only after injection is authorized,
use `--attach` with an explicit target to attach as part of a live command.
Choosing a PID or HWND alone never authorizes injection.

## Inspect inside Sandbox

```powershell
$run = winapp run . --on sandbox --devtools on --detach --json | ConvertFrom-Json
winapp devtools inspect --on sandbox --app $run.appSelector
winapp devtools comments list --status open --json
```

Use `--on sandbox --app <pid>` from fresh guest discovery for live commands;
the emitted `appSelector` remains suitable for scripts tied to one launch.
Names and window titles work when unambiguous. Never fall back to a local app.
The overlay runs in the guest; comments persist
in the host project store. Source snapshots are read-only and project-scoped.
After app exit, use ordinary host comments commands with `--source-root`.
Follow the [Sandbox DevTools workflow](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/devtools-advanced.md#inspect-inside-windows-sandbox)
for supported inputs, options, late attachment and disconnected-save handling.
Sandbox selection is not consent to enable Windows features, install, reboot,
repair a shared guest or start a live test.

## Turn the user's comments into changes

Start with comments the human leaves through the running app's overlay:

```powershell
winapp devtools comments list
winapp devtools comments list --status open --json --source-root <project-dir>
winapp devtools comments get <comment-id>
```

Verify the source locations, make the requested change, and check the result
before resolving the comment:

```powershell
winapp devtools comments update <comment-id> --status resolved --note "Implemented and checked"
```

If `get-source` labels a declaration **likely source**, show the declaration and
its missing-evidence explanation to the user before capturing a source-anchored
comment. Pass `--confirm-likely-source` only after that explicit confirmation;
do not add it automatically to recover from a refused save. See
[source attribution](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/devtools-advanced.md#source-locations) for disk-matched and
likely-source limits, including rebuild/hot-reload uncertainty.

Saved list/get/update/delete use the host store without connecting to Sandbox.
Choose it with `--source-root`, or with `-a <pid>` on list/get/update/delete to use
the running app's project; none of them take `--on`. `--source-root` takes
precedence. If the connection fails, follow the
[comment store recovery instructions](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/devtools-advanced.md#comment-storage-and-advanced-authoring);
do not retry against the current folder's store.
Mutations refresh the markers of running DevTools apps (local and Sandbox) that use
the same store; no `--app` is needed. See the guide for save-warning behavior.

Use current `hits`, not historical `anchor.line`. Runtime text need not appear in
XAML. When `requiresConfirmation` is true, show the ranked candidates and obtain
the user's confirmation before editing; never promote a weak hit by proximity.
See [comment identity](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/devtools.md#review-comments-with-an-agent).
A comment on a Reactor element has `anchor.csharp`: its hit is the C# call site
(`via: call-site` or `line-text` when confirmed; `factory` candidates need
confirmation). Edit that line in the `.cs` file. To anchor a note from source without
the app, use `comments add --file App.cs --line 35 --text ...`; if it lists several
call sites, ask which one and pass `--column`. See
[Reactor comments](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/devtools-reactor.md#leave-comments-on-reactor-code).

A comment that is not linked to source (no `anchor.sourceFile`, often an app with
no project) has no candidates to confirm. Search the whole project, including code
that builds or loads XAML at runtime, for its x:Name, AutomationId, or text
(`anchor.identity.name`, `automationId`, `content`); `context.windowTitle` names the
window it was in. Show the match to the user before editing when more than one place
fits.

For visual requests ("warmer", "less contrast"), read `context` first:
`context.style` is the element's style resource and `context.brushes` lists each
brush's resolved value with its `resourceKey`, `resourceKind` and, for project
styles, the `file` and `line` of the setter. Change that resource or setter instead
of searching for the color. Without a `resourceKey`, the value comes from the
platform's default style; override it locally or with the control's lightweight
styling resource.

Flag unconfirmed anchors instead of guessing. Keep durable comment success
distinct from a marker-refresh warning. Source candidates and line numbers are
hints to verify, not proof of identity. Use `comments add` only when programmatic
comment authoring is explicitly useful; do not create feedback instead of reading
the user's existing comments.

## Inspect, then act

Prefer `inspect`, `search`, `get-property`, `get-layout`, `get-source`, and
`diagnose-binding`. `inspect` and `search` show the confirmed declaration as
`File.xaml:line` (JSON `file`, `line`, `endLine`); an element with only a file
name has no confirmed line. Target an exact PID when multiple apps are running. Reuse a
selector or handle from fresh inspection; never guess an ambiguous element.
For multiple windows, get `--window <HWND>` from
`winapp devtools call Surface.list -a <pid>`. Get `--root <element-handle>` from
window-scoped `inspect`; do not assume `Surface.list`'s `rootHandle` contains the
app's content. The subtree must belong to `--window` when both are supplied.
Never reuse a closed window or stale root, and do not substitute UIA slugs.
Use `search --of-type TextBlock --with 'FontSize>=20' --fields 'Text,FontSize'`
for multi-element reads; repeat `--with` for AND. A definite typed mismatch excludes
an element even if another predicate value is unknown; a local null value does not
match `==` or `*=` with a non-empty value. Without a mismatch, unknown
values still block exact targeting. Check completeness and unevaluated candidates
before acting. Query-targeted `get-property` and
`set-property --value` require exactly one provable match; omit the positional
selector. Follow the [query targeting and timeout rules](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/devtools-advanced.md#query-targeting);
use bounded failure diagnostics to identify unresolved candidates, not to bypass
the uniqueness guard. Never retry an indeterminate write automatically.
For disk-matched source declarations and their limits, follow the
[source-coordinate guidance](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/devtools-advanced.md#source-locations)
before editing XAML or capturing a source-anchored comment.
For a Microsoft.UI.Reactor app, plain `inspect` shows Reactor components (`<Counter>`),
keys, C# names (`Button #incrementButton`, the variable the element was assigned to)
and call sites in the same tree, grouped by window when there are several.
Select Reactor elements with `"<Counter>"`, `"<Counter>/Button"`, `key=t1`,
`App.cs:67` or a unique C# name (`incrementButton` or `#incrementButton`, like an x:Name);
a name with `"nameSource": "csharp-source-text"` was read from the line and may be stale; when a selector matches several elements it fails and lists their
selectors and windows, so pick one or add `-w`. `get-source` returns the C# call
site, the component that rendered it and the statement (`"kind": "csharp"`): edit
that line, not the XAML. Without `csharp`, never edit by line number: follow
`csharpUnavailable`, and for `"authoredState": "ambiguous"` ask which of
`candidates` (the file in the project that defines the component) to use. `get-property` marks values the C# sets (`"setBy": "reactor"`);
a `set-property` on one reports `reactorOwned: true` because Reactor reapplies its
C# value when the component re-renders with a changed value, so change the C# to
keep it. `search --component Counter` lists every instance, and
`search "<Counter>/Button"` lists every element a path selects. If `inspect` notes that
Reactor components cannot be read, follow its reason: `attach` loads the agent into a
running .NET 8+ app. Native AOT, trimmed and attached apps launched with
`winapp run` with DevTools (it sets `REACTOR_DIAGNOSTICS=1`) show the tree, sources, names and
selectors without the agent (`"annotations": "published"`), but state, handlers and logs
then fail with `reactor-unavailable`. Follow the guide's [Reactor guidance](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/devtools-reactor.md)
for build and launch requirements.
To explain a Reactor value, run `get-state <selector>` (props, hooks with their index and
C# name, contexts, where the component is defined) and `diagnose-binding <selector> <property>`,
which follows a C#-set value to the component state that produced it (`"kind": "reactor"`,
`likelySources`). Fix the C#; `set-state <selector> <name|index> <value>`, `rerender` and
`invoke-handler <selector> OnClick` only try a change live and need the user's
authorization like `set-property`. An `indeterminate` result means the write may have
happened: read back with `get-state`, never retry blindly. `get-resource` and
`set-resource --scope app|element` read and live-edit XAML resources in any app.
`winapp ui` commands accept the same Reactor selectors when DevTools is attached, including
`#name`, a C# name and the bracketed selectors an ambiguity error lists.
`get-property <selector>` (no property name) also lists **References**: the
`LabeledBy`, `DescribedBy`, `FlowsTo`, `FlowsFrom` and `XYFocus*` targets from and to the
element (`references` in `--json`), with `cycle: true` when following one comes back.
In a Reactor app, a target that isn't created yet shows as `→ Button (not shown)`
(`pending: true`, no handle); older Reactor versions list only shown targets.
Users moving from Reactor's own DevTools (`mur devtools`, Microsoft.UI.Reactor.Devtools)
should use these commands instead; see the guide's
[migration table](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/devtools-reactor.md#move-from-reactor-devtools).
When a Reactor app misbehaves, start with `winapp devtools logs -a <pid> --json`
(render and swallowed errors, warnings, keyed lists with null or duplicate keys, hot reload);
entries name components and exception types, so read the component's C# for the cause.
A `keyedList` entry means the list re-creates its rows instead of moving them; fix the
item keys at its `file`/`line` when present (the keys themselves are never reported).

Use `set-property <selector> <property> <value>` for an authorized in-memory
change. Report its observed read-back, not the requested value as if it succeeded.
If the result has `replacedBinding`, say so: the local value replaced a `{Binding}`
or overrode an `x:Bind`, and restarting the app restores it. This does not edit source. `call` is for advertised protocol methods without a
curated command; it rejects internal methods and requires `--attach` to inject.

For resource-backed brush edits, pass a fixed color to `set-property`, not
`{ThemeResource key}` or `{StaticResource key}`. To change the reference, edit XAML
and rebuild, or use the inspector's resource editor for a live edit; see the
[resource editing instructions](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/devtools.md#find-and-inspect-an-element).

Before a binding edit or restore, follow the guide's
[binding safety and confirmation workflow](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/devtools-advanced.md#capture-and-restore-a-binding).
Do not treat a forward evaluation as proof of synchronization or an owner refresh
as proof that the selected property was restored.

See the [DevTools guide](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/devtools.md)
and [Advanced DevTools](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/devtools-advanced.md)
for launch restrictions, binding capture/restore, store location, commands and
troubleshooting. Use `winapp devtools <command> --help` for exact options.
