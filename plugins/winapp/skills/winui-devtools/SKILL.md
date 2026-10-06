---
name: winui-devtools
description: Read and change a running WinUI 3 app live with winapp devtools, without modifying source. Use to change, translate, or restyle the running UI, set text or any property at runtime, inspect the live XAML visual tree, properties and bindings, diagnose bindings, or act on review comments left in the app. For clicking, typing, and reading values in any app, use winapp-ui-automation.
---

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
winapp run . --devtools
```

This opens the in-app overlay and waits for the app. Let the user inspect elements
and leave comments there: pick an element, type, press Enter (Shift+Enter for a new
line), or click the next element to save and move on. Ctrl+Shift+F12 moves keyboard
focus to the toolbar, and Esc returns it. Use a second terminal for commands, or add `--detach`
to return to the terminal with the app still running and its PID printed.
Use an exact PID with `-a` when multiple apps are attached.
Never stop another app automatically to make a rebuild succeed.

`--json` is optional output formatting for structured consumers, not the default
agent workflow. It does not hide the overlay. Use `--no-overlay` explicitly for
an authorized headless inspection launch; it requires `--devtools`.
For a script, capture `$run = winapp run . --devtools --detach --json | ConvertFrom-Json`,
then use `winapp devtools inspect -a $run.ProcessId`. After the human leaves a comment, follow
[the comment workflow below](#turn-the-users-comments-into-changes).
If attachment succeeds but the overlay reports a missing XAML resource/type,
see [visual UI prerequisites](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/devtools-advanced.md#visual-ui-prerequisites);
do not mistake unavailable visual UI for failed protocol attachment.

Packaged aliases are prepared automatically in the staged manifest, not manually
authored in the source manifest. DevTools uses its private staged alias regardless
of the ordinary-run `WinAppRunUseExecutionAlias` preference. Explicit
`--without-alias`, Windows-disabled aliases and unverifiable targets still fail.
See the [launch troubleshooting guide](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/devtools.md#when-a-command-fails)
for the `dotnet run` integration limitation.

Do not adopt a different running instance when launch fails. Attaching to an
existing app is explicit and headless by default:

```powershell
winapp devtools list --include-available
winapp devtools attach --pid <pid>
```

Late attach cannot provide startup-only binding hooks or missing XAML source
information. Obtain permission before launching, injecting into, or mutating
an app; do not use an unrelated running app as a test fixture.
Live commands require an attached target. Only after injection is authorized,
use `--attach` with an explicit target to attach as part of a live command.
Choosing a PID or HWND alone never authorizes injection.

## Inspect inside Sandbox

```powershell
$run = winapp run . --on sandbox --devtools --detach --json | ConvertFrom-Json
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
[source attribution](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/devtools.md#find-and-inspect-an-element) for disk-matched and
likely-source limits, including rebuild/hot-reload uncertainty.

Saved list/get/update/delete use the host store without connecting to Sandbox.
Choose it with `--source-root`; list/get reject `--app` and `--on`.
Mutations refresh the markers of running DevTools apps (local and Sandbox) that use
the same store; no `--app` is needed. See the guide for save-warning behavior.

Use current `hits`, not historical `anchor.line`. Runtime text need not appear in
XAML. When `requiresConfirmation` is true, show the ranked candidates and obtain
the user's confirmation before editing; never promote a weak hit by proximity.
See [comment identity](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/devtools.md#review-comments-with-an-agent).

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
an element even if another predicate value is unknown; without a mismatch, unknown
values still block exact targeting. Check completeness and unevaluated candidates
before acting. Query-targeted `get-property` and
`set-property --value` require exactly one provable match; omit the positional
selector. Follow the guide's [query targeting and timeout rules](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/devtools.md#read-or-change-one-query-selected-element);
use bounded failure diagnostics to identify unresolved candidates, not to bypass
the uniqueness guard. Never retry an indeterminate write automatically.
For disk-matched source declarations and their limits, follow the guide's
[source-coordinate guidance](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/devtools.md#find-and-inspect-an-element)
before editing XAML or capturing a source-anchored comment.

To change how something looks in source, start with
`winapp devtools resources explain <selector> <property>`. It names the Style setter
or local value, the resource key, the dictionary and theme branch that define it, and
the file and line to edit. Edit the line named under **Change it** instead of
searching for the color. Keys shown as **WinUI default resources** are not in the
project; override them in `App.xaml` resources rather than editing a style.

To try a different resource value across the whole app, use
`winapp devtools resources set <key> <value>`; every `{ThemeResource}` and
`{StaticResource}` use updates live. `resources list --key <pattern>` shows keys,
values, and their file and line. Changes are in memory only: report the file and line
`set` names so the user can make the change permanent, and run
`winapp devtools resources reset` when done. WinUI default keys can't be set until
the app defines them in `App.xaml` and restarts.

When resource values can't make the change (template, layout, or visual states),
run `winapp devtools resources copy-style <selector>` to preview a copy of the
element's Style (the app's or WinUI's default) in `App.xaml`, then add `--write` to
apply it; `--all-of-type` makes it an implicit Style instead. Prefer overriding
resource keys when they are enough. After writing, edit the copy, then rebuild and
restart the app. Users can do the same from the inspector's STYLE card
(**Edit current**, **Edit a copy…**).

Use `set-property <selector> <property> <value>` for an authorized in-memory
change. Report its observed read-back, not the requested value as if it succeeded.
If the result has `replacedBinding`, say so: the local value replaced a `{Binding}`
or overrode an `x:Bind`, and restarting the app restores it. This does not edit source. `call` is for advertised protocol methods without a
curated command; it rejects internal methods and requires `--attach` to inject.

Before a binding edit or restore, follow the guide's
[binding safety and confirmation workflow](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/devtools-advanced.md#capture-and-restore-a-binding).
Do not treat a forward evaluation as proof of synchronization or an owner refresh
as proof that the selected property was restored.

See the [DevTools guide](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/devtools.md)
and [Advanced DevTools](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/devtools-advanced.md)
for launch restrictions, binding capture/restore, store location, commands and
troubleshooting. Use `winapp devtools <command> --help` for exact options.
