---
name: winapp-sandbox
description: Run, debug, and UI-automate a Windows app inside a persistent Windows Sandbox instead of the user's own desktop, using winapp's --on sandbox option. Use when an agent needs to launch or automate an app without stealing the user's focus, cursor, or keyboard, when UI automation must not disturb the machine it runs on, or when an app should be exercised in a disposable Windows environment. Also covers running arbitrary commands and copying files into that Sandbox.
---
## When to use

- An agent needs to click, type into, screenshot, or record an app **without** taking over the user's desktop
- UI automation must keep running while the user keeps working
- An app should be exercised in a disposable Windows environment and thrown away afterwards
- A dependency has to be installed, or a diagnostic run, inside that environment

Builds still happen on the host and stay fast. Only running, debugging, and automating move.

## Prerequisites

- Windows 11 24H2 or newer, on a supported edition, with hardware virtualization enabled
- An unlocked interactive host session while a command needs real input or screen recording

`--on sandbox` is consent for winapp to install what it needs. On a machine where Windows Sandbox is not
set up, winapp enables the optional feature and installs the Store-delivered client during the same
command. Expect a UAC prompt for the feature, and expect Windows to show its own update UI — which
can take focus — while the client installs. winapp never restarts the machine.

Setup can take minutes. If winapp stops waiting, run the command again: it continues the installation
rather than restarting it.

Missing prerequisites are handled **before** the app is built, and there is **no silent fallback to
running locally** — a command that asked for Sandbox either runs there or fails.

## Common patterns

### Run an app and automate it

```powershell
winapp run . --on sandbox
winapp ui inspect --on sandbox -a MyApp
winapp ui invoke --on sandbox SubmitButton -a MyApp
winapp ui screenshot --on sandbox -a MyApp -o .\result.png
```

The Sandbox stays running between commands and between rebuilds, so the second run transfers only
what changed.

### Capture evidence

```powershell
winapp ui screenshot --on sandbox -a MyApp -o .\before.png
winapp ui record --on sandbox -a MyApp --duration-sec 5 -o .\demo.mp4
```

`-o` lands at the host path given. The file is verified against the size and hash the guest reported
before it is published, so an interrupted transfer never leaves a plausible-looking partial result.

### See the whole Sandbox, not one app

```powershell
winapp target snapshot sandbox                                    # readiness, deployments, guest windows
winapp target screenshot sandbox -o .\sandbox.png                 # the entire guest desktop
winapp target record sandbox -o .\sandbox.mp4 --duration-sec 20   # the entire guest desktop, over time
```

Reach for these when a `ui` command cannot help because there is nothing to name: the app never
appeared, an installer put up a dialog, or a command failed and the reason is somewhere on the
desktop. `ui` verbs capture one application's window from inside the guest; `target` verbs capture the
whole rendered desktop from the host, so the shell and any unnamed window are included.

Start with `winapp target snapshot sandbox`. It inspects only: it never starts a Sandbox, never
connects or reconnects the client, never repairs an unresponsive agent, and never writes to winapp's
own state, so running it can never be what creates or changes the thing it reports. It is stdout-only
and writes no files. It reports guest capabilities separately from effective host-client readiness
(including whether the client is minimized), the deployments winapp made in this Sandbox generation,
and the guest's top-level windows (foreground first, capped at 50, with the true total always shown).
Tracked operation PIDs are revalidated by PID plus start time and are never presented as UI PIDs;
packaged runs identify theirs as package launchers. Guest window titles are printed inert — terminal
escape sequences in a title are
stripped for the human report and preserved verbatim under `--json`. With no Sandbox running it says
so and exits 0 — start one with `winapp run . --on sandbox`.

Prefer `--duration-sec` on `target record`. Without it the recording runs until Ctrl+C or a newline on
redirected stdin, which an unattended script cannot supply. Bad options are rejected with
`target_invalid_arguments` before any Sandbox is started. Warm capture of an already parked client
does not activate or focus it. Cold connection or reconnect can briefly foreground the Sandbox
because Windows may paint it before exact ownership is observable; winapp parks it and restores the
previous foreground at the earliest safe point. `target screenshot` fails rather than bringing the
Sandbox window forward just to get a frame, and publishes by rename so an existing screenshot at the
destination survives a failed capture. A minimized exact winapp-owned client is restored and parked
without activation only when its identity and the caller's foreground can both be re-proven. A
minimized adopted/manual client is left untouched and fails until restored or reconnected. `target record`
holds that promise for the whole take: a client that stops being
capturable mid-take ends the recording with `"stopReason": "capture_unavailable"` and publishes the
frames already captured; a window capture only ever returns black for counts as not capturable, so a
take is never an all-black video reported as a success. `winapp ui record` is unaffected and still
recovers a blank frame from the foreground.

winapp identifies its own client window by parentage — the client is a child of the `wsb connect`
winapp launched — plus start time, since Windows reuses process IDs and a client that predates the
launcher cannot be its child. If Windows will not report that evidence, winapp leaves the client
alone; capture then still works when exactly one client window is open, which winapp adopts without
moving it.

### Iterate

```powershell
winapp run . --on sandbox --detach   # returns once the app is up (see the note below)
winapp ui list-windows --on sandbox  # discover targets
winapp run . --on sandbox --clean    # fresh application data
winapp unregister --on sandbox       # remove just this app from the Sandbox
```

Target unregister follows Windows' one current development registration back to the matching
winapp-managed layout. Stale records from earlier layouts or architectures are repaired
automatically. It removes only Windows' exact reported package full name, confirms absence before
clearing recovery records, and does not require the old layout files to remain. It never removes an
external package with the same identity, and `--force` is not supported with `--on`. If an external
or non-development package currently owns the identity, the command fails and leaves both the
package and winapp's recovery records unchanged.

Several winapp commands can use one Sandbox at the same time, so a foreground `winapp run .
--on sandbox` running in one terminal does not hold up `winapp ui list-windows --on sandbox` in another.
Past eight commands at once the next one fails immediately with `sandbox_agent_busy` instead of
waiting. Deployment and registration still take turns, so two commands never redeploy at once.

With `--detach` on an **unpackaged** app, expect the app to last only as long as the current guest
agent. It is started as a child of the agent, and the agent deliberately contains everything it starts
so a cancelled command cannot strand guest processes holding files the next deployment must replace.
The same containment ends the app when the agent does — including when winapp repairs the agent
automatically, which it does without asking if a later command finds it unresponsive or replaces it
after a winapp upgrade. Nothing reports an error at that moment, because nothing was waiting on the
app. Rerun `winapp run . --on sandbox --detach` to bring it back, and prefer a foreground run when the app
must survive a long automation sequence. A packaged app is activated by Windows rather than started by
the agent and was observed to survive an agent repair that ended an unpackaged one; closing or
restarting the Sandbox still ends both.

### Prepare dependencies or diagnose

```powershell
winapp target exec sandbox -- dotnet --info
winapp target push sandbox .\setup.ps1 Setup\setup.ps1
winapp target exec sandbox --cwd C:\WinApp\work\Setup -- powershell -ExecutionPolicy Bypass -File .\setup.ps1
winapp target pull sandbox Results .\results
```

`-ExecutionPolicy Bypass` belongs in that command. A fresh Sandbox starts at `Restricted`, so a script
you just copied in is refused with `UnauthorizedAccess` without it.

`target push` and `target pull` name the direction in the verb, so it is never guessed and neither
path carries a marker. Symbolic links and junctions in a host source are not followed — only what is genuinely
inside the folder you named is copied.

**Target paths are relative to `C:\WinApp\work`.** That is the folder winapp manages inside the
Sandbox, and resolving every target path against it is what keeps a path you pass from selecting an
arbitrary location. A drive-absolute, rooted, or UNC target path is refused with a message naming the
work root rather than quietly re-rooted, so a copy never reports success for a file that is not where
you asked for it. Each copy into the Sandbox prints the resolved destination — use that as the
`--cwd` of whatever you run next.

A single file lands at exactly the destination you name: `Setup\setup.ps1` *is* the file, not
a folder to place it in. A directory keeps its own structure beneath the destination.

## What to know before relying on it

**The tested build 28000 Sandbox Share UI cannot enumerate Share targets.** Validate Share
source-to-target flows outside Sandbox; see
[Windows Sandbox execution](https://github.com/microsoft/WinAppCli/blob/main/docs/sandbox-execution.md#share-targets-in-the-build-28000-sandbox).

**The Sandbox stays and is reused.** The instance, its agent, and your deployment persist between
commands, so `winapp run . --on sandbox` followed by several `winapp ui ... --on sandbox` commands is one
environment, not several. A later command reconnects to the agent that is already running rather than
restarting it, and read-only verbs (`inspect`, `search`, `get-property`, `get-focused`,
`list-windows`, `wait-for`, `status`) do **not** reconnect the Sandbox window — so they never
interrupt what is on screen. If the agent has stopped, the next command repairs it inside the same
Sandbox; your deployment survives.

**Slow phases report progress on stderr.** Starting or reusing the Sandbox, preparing the agent,
checking runtimes, deploying, and launching each print a line before they begin. Under `--json`,
stdout still carries exactly one machine-readable document — progress never goes there.

**No firewall prompt.** The host assigns the agent's port and creates a narrow inbound rule for that
program and port before the agent starts listening, so Windows never raises its consent dialog inside
the Sandbox.

**Upgrading winapp needs a fresh Sandbox.** A running agent holds the staged binary, so a newer
winapp cannot replace it in place. It says so and asks you to close the Sandbox rather than failing
with a file error.

**Targets must be explicit.** `--app` or `--window` is required, exactly as locally. Use
`winapp ui list-windows --on sandbox` to discover them; no command infers the most recent launch.

**Guest process IDs and window handles are scoped.** They are valid only inside the Sandbox
generation that produced them. After the Sandbox is recreated, stale values are rejected rather than
resolved against the new one.

**An app name, PID, or window handle is always read on the selected target.** None of them carries
a scope of its own, so `--on sandbox` is what says where to look — `--on sandbox -a MyApp`,
`--on sandbox -a 4212`, `--on sandbox -w 123456`. Without it, the same values name something on
this desktop.

**Every run option keeps its meaning** — `--detach`, `--debug-output`, `--no-launch`, `--clean`,
`--unregister-on-exit`, `--with-alias`, `--json` — because the Sandbox runs the ordinary
`winapp run`. Two limits: `--debug-output` is not available for an *unpackaged* app in Sandbox and is
refused up front, and `--detach` on an unpackaged app gives you a process that lasts only as long as
the current guest agent, as described under *Iterate* above.

**`--on sandbox` isolates the running app, not the build.** Project evaluation, restore, and compilation
happen on the host, so it does not make an untrusted project safe to open. Everything inside the
Sandbox is one trust boundary: apps sharing it share the user account, desktop, registry, runtimes,
and network, and can interfere with one another.

**Shared runtimes are provisioned from your caches.** Before deploying, winapp reads the app's
package-manifest dependencies, unpackaged Windows App SDK version from `*.deps.json`, and
`*.runtimeconfig.json`, then stages and installs what the Sandbox is missing — never on your machine,
and never over a version already registered. A Windows App
Runtime dependency brings its whole cached inventory (Framework, DDLM, Main, Singleton), and shared
.NET runtimes are unpacked from official runtime packs into a per-user .NET root inside the guest,
with the app launched against it. The one package winapp downloads — the desktop VC runtime — must
pass an Authenticode Microsoft-signature check *and* an identity/version/architecture/publisher check
before it is cached, so a rejected payload never poisons the host cache. The complete graph is
verified before every launch, because
`target exec` can change guest runtime state between runs. Anything that cannot be satisfied fails
with `sandbox_runtime_provision_failed` naming it, before launch. Publishing self-contained avoids
the requirement entirely.

**A Sandbox that is already running is used, not refused.** Windows allows one at a time, so if one is
up — started by hand, left by an earlier command, or opened by the client installer — winapp prepares
that one. Preparing it maps winapp's bootstrap folders into the guest, connects its client, turns on
Developer Mode, and adds an inbound firewall rule for the agent, so anything already running there
shares the session with what winapp deploys. Nothing existing is removed, and **winapp never stops a
Sandbox** — use `wsb list`, `wsb connect --id <id>`, `wsb stop --id <id>`.

**The Sandbox window must stay connected** for real input and screen recording. winapp connects a
client only when the guest has no interactive session yet — connecting one that already has a client
would start a second window, not reuse yours. If you closed the window, the session survives it, so
winapp only finds out when the guest agent reports no input desktop; it then reconnects for you once,
off-screen and without stealing focus. If that still leaves no input desktop it reports
`sandbox_input_not_ready` with a `wsb connect` command rather than reconnecting again.

## Troubleshooting

| Error code | What it means | What to do |
|---|---|---|
| `sandbox_unsupported` | This machine cannot run Windows Sandbox | Check the edition and that virtualization is enabled in firmware |
| `sandbox_setup_requires_elevation` | The UAC prompt was declined, or there was no session to show one in | Run the `dism.exe` command in the error from an elevated terminal, then retry |
| `sandbox_setup_requires_restart` | The feature is enabled; Windows needs a restart | Restart, then run the command again |
| `sandbox_setup_incomplete` | Windows is still installing the Sandbox client | Wait, then run the command again — retrying resumes it |
| `sandbox_setup_failed` | Windows refused to enable the feature or start the client | Check edition, firmware virtualization, and optional-feature policy |
| `sandbox_unmanaged_instance` | A running Sandbox could not be prepared, or more than one is running | Wait for it to finish starting and retry, or close the ones you do not need |
| `sandbox_no_interactive_session` | No interactive guest session | Reconnect the Sandbox window with `wsb connect` |
| `sandbox_input_not_ready` | Input could not be delivered — and none was reported as delivered | If the client is minimized, restore that existing window manually; if it is disconnected, reconnect it |
| `sandbox_terminated` | The Sandbox went away mid-command | Retry; the next command recreates and redeploys |
| `sandbox_deployment_dirty` | The guest copy is incomplete, so it will not launch | Run the command again to redeploy completely |
| `sandbox_transfer_interrupted` | A transfer stopped; nothing was published | Retry. The error names the artifact, expected size, and what arrived |
| `sandbox_agent_incompatible` | The guest agent needs a newer winapp, or a different winapp version is already running it | `winapp update`. If you upgraded winapp while a Sandbox was running, close the Sandbox so a fresh agent starts |
| `sandbox_agent_busy` | Eight winapp commands are already using this Sandbox | Wait for one to finish, then retry |
| `sandbox_runtime_provision_failed` | A runtime the app requires is missing from the Sandbox | The error names it. Publish self-contained, or install it with `winapp target exec sandbox` |
| `sandbox_target_ambiguous` | On a `target snapshot`/`screenshot`/`record`, more than one Sandbox client window is running and none is provably winapp's | The error names the candidate process IDs. Close the Sandbox windows you do not need, then retry |
| `sandbox_artifact_failed` | An output could not be produced — for example, the exact minimized Sandbox client could not be restored without activation, or the window returned no usable pixels | Restore the Sandbox window manually, then retry |
| `target_invalid_arguments` | The `target` command line was rejected before it ran, for example a mistyped option | The message says what was wrong. Under `--json` this arrives as the usual error envelope on stderr, not as help text |

Infrastructure failures use codes distinct from your app's exit codes, so "winapp could not run your
app" is always distinguishable from "your app failed".

Two problems report no winapp error code at all:

| Symptom | Cause | What to do |
|---|---|---|
| A detached **unpackaged** app is gone, and no command reported it stopping | It was started by the guest agent and ended with it, most often because winapp automatically repaired the agent | Rerun `winapp run . --on sandbox --detach`. For an app that must survive a long automation sequence, run it in the foreground instead |
| A script copied in with `target push`/`target pull` fails with `UnauthorizedAccess` | A fresh Sandbox starts with the PowerShell execution policy at `Restricted` | Run it as `powershell -ExecutionPolicy Bypass -File .\script.ps1` |

## Full documentation

See [Windows Sandbox execution](https://github.com/microsoft/WinAppCli/blob/main/docs/sandbox-execution.md)
for the lifecycle, deployment, coordination, and architecture details.
