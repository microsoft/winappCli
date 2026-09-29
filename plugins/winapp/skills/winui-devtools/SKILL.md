---
name: winui-devtools
description: Inspect WinUI XAML, diagnose bindings, try live dependency-property changes, and act on source-anchored review comments with winapp DevTools. Use for WinUI 3 apps built with the Windows App SDK, rather than general Windows UI Automation.
---

## Start with the target

Use `winapp ui` and the `winapp-ui-automation` skill for general Windows UI
Automation: actions, screenshots, waits and accessibility across app frameworks.
DevTools is for WinUI 3 XAML trees/source, bindings and dependency-property
diagnosis or edits. UIA selectors are not DevTools selectors; copy a fresh
selector from `winapp devtools inspect` or `search`.

```powershell
winapp run . --devtools
```

This opens the in-app overlay and waits for the app. Let the user inspect elements
and leave comments there. Use a second terminal for commands, or add `--detach`
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

Flag unconfirmed anchors instead of guessing. Keep durable comment success
distinct from a marker-refresh warning. Source candidates and line numbers are
hints to verify, not proof of identity. Use `comments add` only when programmatic
comment authoring is explicitly useful; do not create feedback instead of reading
the user's existing comments.

## Inspect, then act

Prefer `inspect`, `search`, `get-property`, `get-layout`, `get-source`, and
`diagnose-binding`. Target an exact PID when multiple apps are running. Reuse a
selector or handle from fresh inspection; never guess an ambiguous element.
For multiple windows, get `--window <HWND>` from
`winapp devtools call Surface.list -a <pid>`. Get `--root <element-handle>` from
window-scoped `inspect`; do not assume `Surface.list`'s `rootHandle` contains the
app's content. The subtree must belong to `--window` when both are supplied.
Never reuse a closed window or stale root, and do not substitute UIA selectors.
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
