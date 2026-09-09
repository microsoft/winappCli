<!-- mslearn: true -->
<!-- ms.topic: concept-article -->
<!-- description: Run, debug, and UI-automate Windows applications inside a persistent Windows Sandbox using the winapp CLI --on sandbox option. -->
# Windows Sandbox execution

Build on your machine, then run and automate the app inside a persistent Windows Sandbox.

> [!NOTE]
> This feature is in development. `winapp run --on sandbox`, `winapp unregister --on sandbox`,
> `winapp ui ... --on sandbox`, `winapp target exec sandbox`, and `winapp target push sandbox` work. This page
> documents the behaviour and its failure modes so they are reviewable alongside the code.

## Why

Windows UI automation normally runs on your own desktop. An automated workflow can steal focus,
move your cursor, type into the wrong window, or simply require you to stop using the machine
while it runs.

Sandbox execution moves the application and its automation into an isolated Windows session,
while builds stay on the host and stay fast.

```powershell
winapp run . --on sandbox
winapp ui inspect --on sandbox -a MyApp
winapp ui invoke --on sandbox SubmitButton -a MyApp
```

The Sandbox stays running between commands and between rebuilds, so a rerun transfers only what
changed.

## What `--on sandbox` does and does not protect

`--on sandbox` isolates the **running** application. It does **not** make an untrusted project safe
to open: project evaluation, package restore, and compilation all happen on the host.

Everything inside the Sandbox is one trust boundary. Applications and workflows sharing it also
share the user account, the desktop, registry and package state, installed runtimes, and network
access, and can observe or interfere with one another. Isolating two workflows from each other
needs two machines.

The host-to-guest connection is treated as untrusted regardless: every boot gets a fresh identity
and secret, the guest serves commands only after an authenticated and encrypted handshake, requests
carry structured argument arrays rather than interpolated command lines, and every path is
canonicalized and confined to a managed root.

### What path containment does and does not guarantee

Paths crossing into the guest are canonicalized, confined to a managed root, and refused if any
component is a reparse point — a junction or symbolic link. Managed folders are never enumerated
through one either, so a link cannot make content outside a managed root appear to be inside it.

The same rule applies on the **host** side, to the folder a deployment or `target push`/`target pull` reads from.
Those folders are walked one level at a time with every directory tested before it is descended
into, **starting with the root itself** — the per-entry check never sees the root, because the walk
begins by enumerating the root's contents rather than by looking at it, so a root that is a junction
would otherwise be followed wholesale. A file-level check alone would not be enough either: a file
reached *through* a directory junction is an ordinary file and carries no reparse attribute, so
`build\logs` pointing at `C:\Users\you\.ssh` would otherwise be hashed and copied into the guest as
`build\logs\id_rsa`. A junction that points back at its own ancestor ends the walk for the same
reason, instead of recursing until the path length gives out. Deployment refuses such a folder
outright; `target push`/`target pull` treats a link inside the folder as absent and copies only what is genuinely
inside the folder you named. A linked *root* is refused by both, because copying nothing while
reporting success is a worse answer than saying so.

That reliably stops a link that **already exists** in the path, which is how one realistically
appears: left behind by an earlier deployment, an application, or an extracted archive. Every
component is also re-checked immediately before a file is opened for hashing or copying —
**including the file itself**, not just the directories above it. That last part is load-bearing:
enumeration checks a file's reparse state, but the read does not repeat it, because opening a
symbolic link follows it to its target like any other open. Without it, a file replaced by a link
between the walk and the read would be read straight out of the tree — and a zero-byte file whose
replacement keeps its timestamp would not be caught afterwards by the "changed while preparing"
guard either, since that one stats without following and would see a matching length and time.

It is **not** proof against a co-resident guest process that replaces a verified component with a
link in the moment between that final check and the open. Closing that race requires
handle-relative, no-follow file opens on every component, which v1 does not implement.

That residual race is accepted deliberately, and it is consistent with the trust model above rather
than an exception to it: a guest process able to win it can already terminate the agent, edit the
deployment directly, or interfere with the application, because everything in the Sandbox runs as
the same user. It is not the weakest link. Workflows that must be isolated from one another need
separate machines.

## Running an app

```powershell
winapp run . --on sandbox
winapp run .\MyApp.csproj --on sandbox --detach --json
winapp run .\publish --on sandbox --clean
```

The app is built and its package layout is produced on the host, exactly as a local run would
produce it. Nothing is registered on your machine and no runtime is installed on it: the layout is
transferred into the Sandbox, and guest `winapp` registers, launches, and — with `--debug-output` —
debugs it there.

After a packaged app starts, the command prints the guest PID and a scoped UI target you can copy:

```text
Started the application in Windows Sandbox (PID: 4212).
UI target: --on sandbox -a 4212
Waiting for the application to exit...
```

With `--detach`, the first two lines are printed and the command returns instead of waiting. Use the
whole `--on sandbox -a 4212` target with `winapp ui`; a Sandbox PID is not meaningful on the host.

Every existing run option keeps its meaning, because the guest runs the same `winapp run` you would
have run locally:

| Option | In Sandbox |
|---|---|
| `--detach` | Returns after the guest launch; the app and the Sandbox keep running. For an *unpackaged* app the launched process is tied to the guest agent's lifetime — see [Detached apps and the agent's lifetime](#detached-apps-and-the-agents-lifetime) |
| `--debug-output` | Debugs inside the guest and streams its output back |
| `--no-launch` | Deploys and registers in the Sandbox without launching |
| `--clean` | Clears that guest package's application data, and redeploys from scratch |
| `--unregister-on-exit` | Unregisters that guest package once its process exits; the Sandbox stays |
| `--with-alias` | Launches the guest execution alias with forwarded standard streams |
| `--json` | stdout stays machine-readable; progress goes to stderr |

Build options — `--configuration`, `--arch`, `--framework`, `--property`, `--no-build`,
`--no-restore` — still apply on the host, before anything is transferred.

### Share targets in the build 28000 Sandbox

The tested Windows Sandbox image on OS build 28000 cannot enumerate Share targets: the Share UI
fails before showing any targets. The identical packaged desktop app, source, payload, and loose
registration work on a build 28000 host, while the Sandbox still fails with a successfully indexed
`windowsApp` control package. This is a limitation of the tested Sandbox environment, not winapp
registration or a specific application runtime model. Use Sandbox to test the rest of the app, but
validate Share source-to-target flows outside Sandbox.

### Detached apps and the agent's lifetime

`--detach` returns as soon as the app is running, and the Sandbox is not shut down afterwards. For an
**unpackaged** app there is one limit worth knowing before you rely on it:

```powershell
winapp run .\publish --on sandbox --detach   # returns; app is running in the Sandbox
```

That process runs for as long as the **current guest agent** does. winapp starts it as a child of the
agent, and the agent contains every process it starts so that cancelling a command cannot leave
orphaned grandchildren holding files the next deployment has to replace. The same containment means
that when the agent goes away, the processes it started go with it.

The agent goes away when the Sandbox is closed or restarted, and also when winapp repairs it — which
happens automatically, without asking, if a later command finds it unresponsive or needs to replace it
after a winapp upgrade. So a detached unpackaged app can be gone by the time you come back to it, with
no error at the moment it stopped, because nothing was waiting on it.

If a later command reports the app is no longer running, rerun it:

```powershell
winapp run .\publish --on sandbox --detach
```

A **packaged** app is launched through Windows' own activation rather than as a child of the agent, and
was observed to keep running across an agent repair that ended an unpackaged one. Closing or restarting
the Sandbox still ends everything inside it, packaged included.

This is a property of what the launched process is a child of, not of how the run was reported, so
`--json` does not change it.

### Progress, and why the terminal is never silent

Preparing a Sandbox is a chain of multi-second operations, and a terminal that prints nothing for
that long is indistinguishable from a hang — which usually ends with the user killing the command
part-way through a deployment. Every slow phase therefore announces itself *before* it starts:
installing prerequisites, checking availability, starting, reusing, recovering, or taking over the
Sandbox, repairing or preparing the agent, connecting to it, checking runtimes, deploying the
application, and starting it.

All of that goes to **standard error**, never standard output. That is what keeps `--json` honest: a
scripted caller still gets exactly one machine-readable document on stdout, and a terminal user still
sees what is happening.

### Reuse across commands

The Sandbox, its agent, and the deployment survive between commands, so a `run` followed by several
`ui` commands is one environment rather than several. The connection material is written to the
target's bootstrap folder, so the *next winapp process* reconnects to the agent that is already
serving instead of restaging and relaunching it — reuse that in-process state alone could never
provide, because every CLI invocation is a new process.

Two consequences are deliberate:

- **The Sandbox client is not reconnected for a read-only command.** `wsb connect` against an
  instance whose client is already up ends that session and asks the user whether to reconnect, so it
  is used only when there is no session yet, or when the verb genuinely needs real input or screen
  capture. `ui inspect`, `search`, `get-property`, `get-focused`, `list-windows`, `wait-for`, and
  `status` read UI Automation state and do neither.
- **A failed reconnect repairs the agent, never the Sandbox.** If the agent has stopped, the next
  command restages and relaunches it inside the same instance. The epoch is unchanged, so deployment
  and runtime state stay valid across the repair, and nothing running in the guest is discarded.

Upgrading winapp while a Sandbox is running is the one case that cannot be repaired in place: the
running agent holds the staged binary open, so the new version reports that plainly and asks you to
close the Sandbox, rather than failing with a file-sharing error.

### The guest agent's network exposure

The host assigns the agent's TCP port before the agent starts, writes it into the read-only bootstrap
material, and creates the inbound allow rule for that exact port and program *before* launching the
agent.

The ordering is the point. Windows raises its "Windows Firewall has blocked some features of this
app" consent dialog at the instant a program binds a listening socket with no matching rule, so a
rule created after the agent reports its port — however narrow — arrives too late to prevent a prompt
the user then has to answer inside the Sandbox window. Letting the agent bind port 0 and reporting
back makes that ordering impossible, which is why the host chooses the port.

The rule is scoped to one program, one protocol, one direction, and one port in the dynamic range,
and rules from earlier boots are removed rather than accumulating. Reachability is not authorisation:
every frame is still authenticated and encrypted with the per-boot pre-shared key.

Unpackaged apps run too: there is no package to register, so the build output is deployed and the
app's executable is started in the guest. Its working directory is the deployed folder, because the
host directory you ran from does not exist there. `--debug-output` is not available for an
unpackaged app in Sandbox and is refused up front rather than silently producing nothing.

Under `--json`, Sandbox runs add fields to the existing document and change none of the existing
ones:

```json
{
  "AUMID": "Contoso.MyApp_8wekyb3d8bbwe!App",
  "ProcessId": 4212,
  "Sandbox": true,
  "ProcessScope": "sandbox",
  "UiTargetArgs": "--on sandbox -a 4212",
  "ExecutionTarget": {
    "Kind": "sandbox",
    "Id": "default",
    "Architecture": "arm64",
    "Epoch": "..."
  }
}
```

`ProcessId` is a **guest** process ID and is meaningful only within `ExecutionTarget.Epoch`. A value
from a previous generation is rejected rather than resolved against whatever Sandbox exists now.

## Removing an app

```powershell
winapp unregister --on sandbox
winapp unregister --on sandbox --manifest .\Package.appxmanifest
```

This removes only the package a matching deployment registered. Windows' current development
registration and its registered location select the owning deployment, so rerunning the same package
identity from another output path or architecture does not leave `unregister` ambiguous. After a
successful removal of that exact package full name, winapp queries Windows again and clears stale
ownership claims only after confirming the registration is gone. If removal or verification fails,
the claims remain for a safe retry. If Windows initially reports no registration, winapp clears stale
claims and reports that nothing is registered. Removal does not depend on the old layout files still
being present.

The guest must still confirm that the current registration is a development package rooted in a
current-generation winapp-managed folder. A package you installed in the Sandbox yourself is never
touched, even when its identity matches; unregister fails and tells you to remove that package in the
Sandbox instead. `--force` is rejected with `--on`; target ownership checks cannot be bypassed.

## Automating the UI

```powershell
winapp ui inspect --on sandbox -a MyApp
winapp ui invoke --on sandbox SubmitButton -a MyApp
winapp ui screenshot --on sandbox -a MyApp -o .\result.png
winapp ui record --on sandbox -a MyApp --duration-sec 5 -o .\result.mp4
```

Every `ui` verb accepts `--on sandbox`. The command is intercepted once, before any local UI service
runs, and forwarded whole to guest winapp — so the host performs no UI Automation, window discovery,
capture, or input injection. That is the point: a Sandbox workflow cannot steal your focus, move your
cursor, or type into your windows.

A string app target can opt in by prefix instead:

```powershell
winapp ui inspect --on sandbox -a MyApp
winapp ui inspect --on sandbox -a 4212
```

A numeric `--window` is left alone and needs `--on sandbox`, because a window handle carries no scope of
its own — inferring one would resolve a host window against the guest, or the reverse.

Targeting is unchanged: if neither `--app` nor `--window` is given, the command fails and lists guest
targets rather than guessing. `winapp ui list-windows --on sandbox` is the discovery path.

Guest process IDs and window handles are valid only inside the execution target's current epoch, and
values from a previous generation are rejected rather than resolved against a recreated Sandbox.

### Output files

`-o/--output` is redirected into per-command guest staging, and the file is brought back after the
command succeeds. It reaches the path you asked for only after its size and hash match what the guest
reported, and it is published by rename — so an interrupted transfer never leaves a shorter but
plausible screenshot or a truncated video where a complete one is expected, and never overwrites what
was already there. A failure names the artifact, its expected size, how much arrived, and the phase
that failed.

Transfers restart rather than resume. The result the command prints reports your path, not the guest
staging path it was actually written to.

### Seeing the whole Sandbox

`ui` verbs answer questions about one application. When you need to know what the *Sandbox* is doing —
because a command failed, an installer put up a dialog nobody named, or the app never appeared at all —
three `target` verbs describe and capture the desktop as a whole:

```powershell
winapp target snapshot sandbox
winapp target screenshot sandbox -o .\sandbox.png
winapp target record sandbox -o .\sandbox.mp4 --duration-sec 20
```

These are the mirror image of the `ui` verbs. A `ui` capture is performed by guest winapp and returns
one application's window; a `target` capture is performed on the **host**, against the Sandbox client
window itself, and returns the entire rendered guest desktop — shell, dialogs, and anything that
appeared before it could be named. Nothing is staged in the guest and nothing is transferred back,
because the pixels were on your machine the whole time.

The client window winapp parked off-screen is captured where it is. If that exact client is minimized,
winapp restores it with `SW_SHOWNOACTIVATE`, parks it off-screen again with `SWP_NOACTIVATE`, and verifies
that its process identity is unchanged and the previous foreground window remained foreground. If any
check fails, capture fails without publishing a misleading minimized thumbnail.

`winapp target snapshot` goes further: it never starts a Sandbox, never connects or reconnects the
client, and never repairs an unresponsive agent, so asking what the Sandbox is doing can never be what
makes it do something. With no Sandbox running it reports that and exits 0. Start one with
`winapp run . --on sandbox`.

`winapp target record` resolves the client window and then releases the guest connection before the
take begins, because the recording runs entirely on the host. A recording that lasts hours does not
occupy the Sandbox's single guest connection for hours. It holds the same promise as a screenshot for
its whole length: the client window is never brought to the front or activated to rescue a frame. A
minimized client is restored and parked without activation before the take; a client that stops being
capturable mid-take ends the recording and publishes what it captured with the stop reason
`capture_unavailable`. A window that capture only
ever returns black for counts as not capturable, so a Sandbox recording is never an all-black video
reported as a successful take. Recording an app you are watching with `winapp ui record` is
unchanged.

winapp captures only the client window it knows it created or adopted, identified by handle, process
ID, and process start time together, and remembers it across invocations. A window winapp creates is
identified by **parentage**: Windows Sandbox starts the client as a direct child of the `wsb connect`
process winapp launched, so the client whose parent is that launcher is winapp's, however many other
connects are running at the same moment. Nothing is claimed on timing or on being the newest window.

Parentage is checked together with **age**, because Windows records a process's parent ID once and
never revises it: a launcher exits, Windows reuses its process ID, and a client started hours earlier
by something else can end up naming winapp's launcher as its parent. A client that already existed
before winapp's launcher started therefore cannot be that launcher's child, and winapp requires the
client to be no older than the launcher it names before claiming it. Where either start time cannot
be read, nothing is claimed.

When that evidence is missing — Windows would not report the parent, or no client with the right
parent appeared — winapp claims nothing: the client is left visible where the Sandbox put it and is
not recorded. Capture then still works if exactly one client window is open, which it **adopts**:
read where it stands, never moved, and reported as adopted. Windows can leave extra
`WindowsSandboxRemoteSession` processes behind, so when more than one is running and none is provably
winapp's, these verbs fail with `sandbox_target_ambiguous` and name the candidate process IDs instead
of capturing a window that may belong to something else.

See [`target snapshot`](usage.md#target-snapshot), [`target screenshot`](usage.md#target-screenshot),
and [`target record`](usage.md#target-record) for the full options.

## Requirements

- Windows 11 24H2 or newer, on a supported edition
- Hardware virtualization enabled

`--on sandbox` is your consent for winapp to install what it needs. If Windows Sandbox is not set up
yet, winapp enables the optional feature and installs the Store-delivered Sandbox client for you, in
the same command. Two things you should expect while that happens:

- **Windows asks for permission.** Enabling the feature raises the standard UAC prompt. winapp never
  restarts your machine — if Windows says a restart is required, the command stops and tells you.
- **Windows shows its own UI.** Installing the client is the OS's "Downloading and installing
  updates" flow, so a window can appear and take focus. That window belongs to Windows, not to
  winapp, so winapp cannot keep it in the background.

Setup can take several minutes, and winapp keeps saying so on standard error while it waits. If it
gives up waiting, the installation usually keeps running in the background — **run the command again
and it continues where it left off** rather than starting over.

Only these need you:

| What you see | What it means | What to do |
|---|---|---|
| `sandbox_setup_requires_elevation` | You declined the UAC prompt, or there was no interactive session to show one in | Run the `dism.exe` command in the error from an elevated terminal, then retry |
| `sandbox_setup_requires_restart` | The feature is enabled; Windows needs a restart | Restart, then run the command again |
| `sandbox_setup_incomplete` | Windows is still installing the client | Wait, then run the command again |
| `sandbox_setup_failed` | Servicing refused | Check the edition, that virtualization is on in firmware, and that policy allows optional features |
| `sandbox_unsupported` | This host cannot run Windows Sandbox | Use a Windows 11 machine on a supported edition |

Missing prerequisites are handled **before** your application is built, and there is no silent
fallback to running locally — a command that asked for Sandbox either runs there or fails.

Real input and screen capture additionally need an unlocked interactive host session.

## Lifecycle

Windows permits one Sandbox at a time, so `--on sandbox` uses that one.

If a Sandbox is already running when you run a winapp command — because you started it yourself,
because a previous command left it up, or because the client installer opened one — winapp uses it
instead of asking you to close it.

**Using an existing Sandbox changes it.** winapp maps its bootstrap folders into that guest, connects
its client, turns on Developer Mode, and adds an inbound firewall rule for its agent. Whatever is
already running in that guest shares the session with what winapp deploys, which is the same trust
boundary [described above](#what---on-sandbox-does-and-does-not-protect). Nothing already in the guest is
removed, and **winapp never stops a Sandbox** — not on success, not on failure, and not for one it
took over.

Each command that prepares a guest gets its own bootstrap folders, named per generation, so a folder
or agent left by an earlier generation is never mistaken for the current one.

You manage the Sandbox with the Windows Sandbox CLI:

```powershell
wsb list
wsb connect --id <id>
wsb stop --id <id>
```

If more than one Sandbox is somehow running, winapp stops and reports the IDs rather than guessing
which one you meant.

If you close the Sandbox or run `wsb stop` while commands are running, they fail with
`sandbox_terminated`. Process IDs, window handles, deployments, and package records from the old
generation are invalidated rather than resolved against whatever is created next.

### When a start half-succeeds

winapp picks a random instance ID and records it *before* asking Windows to start the Sandbox. If the
start reports an error but has in fact created the instance — which `wsb start` does on some hosts,
with `0x80070002` — winapp recognises that exact instance and takes ownership of it.

The record survives the command, so if winapp is killed mid-start, the next command finishes the job
instead of trying to start a second Sandbox. Recovery always matches the ID winapp asked for, never
"whichever Sandbox appeared", so a Sandbox somebody else started is never claimed as winapp's.

### Two winapp state roots on one machine

`WINAPP_TARGET_STATE_ROOT` gives a winapp process its own ownership record. Two processes pointed at
different roots cannot see each other's, so the second one treats the running Sandbox as one nobody
is managing and prepares it for itself.

That is additive, not destructive: the second manager gets a fresh generation with its own bootstrap
folders, its own agent port, and its own connection material, and it neither stops the Sandbox nor
removes the first manager's firewall rule or shares. What it cannot do is coordinate — the locks that
serialize two winapp commands live in the state root, so redirecting the root opts out of them. Use
one state root per machine unless you specifically want independent managers.

### The Sandbox window must stay connected

Real input and screen recording require the Sandbox's remote-session client to be connected and
not minimized. Once connected, winapp keeps the window it owns off-screen and at the bottom of the
window order without activating it. A cold connection or reconnect has the brief-foreground ceiling
described below.

If winapp's exact owned client is minimized before a real-input or capture command, winapp restores
it immediately before the operation. The restore is nonactivating and is accepted only when the same
handle, process ID, and process start time remain live, the window is no longer minimized, and the
previous foreground window is unchanged. A minimized adopted/manual client is never moved off-screen;
restore or reconnect that window manually. Read-only UI inspection does not restore or move any
minimized client.

winapp connects a client only when the guest does not already have an interactive session. If you
already had the Sandbox open, that window keeps being the one you see: connecting again would start
a **second** client rather than reuse yours, and the extra one outlives `wsb stop`.

If you closed the Sandbox window, the guest session survives it — so winapp cannot tell from the
session alone that the window is gone. It finds out when the guest agent reports it has no input
desktop, and at that point it reconnects for you, **once**, placing the new window off-screen and
restoring the previous foreground at the earliest safe point. If the guest still has no input desktop after that, winapp stops and
reports `sandbox_input_not_ready` with a `wsb connect` command rather than reconnecting again.

On a cold connection, Windows can assign foreground and paint the client before its top-level
CREATE/SHOW accessibility event is delivered. winapp begins exact-parent polling as soon as its own
`wsb connect` launcher starts and parks the client at the first provable opportunity, then restores
the previous foreground if Windows took it. This minimizes the visible interval, but Windows exposes
no background-connect option and does not provide a hook early enough to guarantee zero first paint.

Closing that window has a specific and initially surprising effect, established by measurement on
Windows 11 ARM64:

| Capability | Client connected | Client closed |
|---|---|---|
| Guest session and running apps | works | works |
| UI Automation inspection and UIA-pattern actions | works | works |
| Real input (`SendInput`, mouse) | works | **fails** |
| Windows Graphics Capture recording | works | **fails** |

So inspection keeps working in exactly the state where input must be refused. winapp reports that
as `sandbox_input_not_ready` rather than reporting input it did not deliver. Reconnecting with
`wsb connect` restores the same guest session, the same running applications, and both capabilities.

## Shared runtimes

Before anything is deployed, winapp works out what the app needs at runtime and makes sure the
Sandbox has it. Requirements are read from what the build already produced, not re-derived: framework
dependencies declared in the package manifest, the exact Windows App SDK package recorded in an
unpackaged app's `*.deps.json`, and shared .NET frameworks named in `*.runtimeconfig.json`. These are
the same artifacts registration and apphost startup consume.

They are treated as **compatible constraints** — "this package, at this version or newer" — rather
than an exact machine snapshot. That is what lets winapp leave a runtime another app in the same
Sandbox is already using exactly as it is.

Payloads come from your caches first. The Windows App Runtime packages you already restored to build
the app are staged into the guest over the same verified file channel the app itself uses, so a
framework-dependent app no longer needs guest network access on first run. Only when no cached
payload satisfies the constraint does winapp acquire one, through the same download path
`winapp restore` uses, and cache it on the host.

A Windows App Runtime dependency resolves to the **whole runtime**, not just the package a manifest
names. A manifest declares only the Framework, but a WinUI app also needs the DDLM that lets an
unpackaged process find the runtime, plus Main and Singleton. winapp takes the complete inventory
from the cached runtime whose Framework satisfies the constraint, reads each package's real identity
from its own manifest — the DDLM's name and the Singleton's version do not follow from the
Framework's — and stages, installs, and verifies all of them.

Unpackaged Windows App SDK builds declare no package dependency. For those, winapp reads the exact
`Microsoft.WindowsAppSDK` version from the build's `*.deps.json` and selects that version's cached
runtime inventory, so the bootstrapper finds the same Framework and DDLM the app was built against.

Shared .NET runtimes are provisioned too. winapp builds a portable layout from an official payload
it already has: a .NET installation on your machine, or the
`Microsoft.NETCore.App.Runtime.win-{arch}` and `Microsoft.WindowsDesktop.App.Runtime.win-{arch}`
runtime packs in your NuGet cache, restoring the exact matching pack when neither is present. The
layout is cached on the host, staged into the guest, and unpacked side-by-side into a per-user .NET
root winapp owns under the guest's managed folder. Nothing machine-wide is touched, no elevation is
taken, and the app is launched with `DOTNET_ROOT` pointing at that root — but only when the managed
root is what actually satisfies a framework. A Sandbox that already has the runtime is left alone.

`DOTNET_ROOT` is exclusive: an apphost pointed at a root resolves every framework from there and
consults nothing else. So it is all from one root or none from it. A guest that can serve part of
the graph but not all of it gets the whole graph installed into the managed root.

A desktop app's runtime configuration names only `Microsoft.WindowsDesktop.App`, which cannot load
without `Microsoft.NETCore.App` beneath it, so the core runtime is provisioned with it. Version
matching follows .NET's own roll-forward: a newer patch or minor of the same major is compatible, a
different major is not.

Identity is matched the way Windows matches it. A requirement carries the publisher and the exact
architecture, and only a registered package with the same name and publisher, at or above the
required version, in that architecture or genuinely neutral, satisfies it. An x86 package never
satisfies an x64 dependency.

Installation happens in the guest, under the same lock every other guest mutation takes, and is
journaled before the first package is touched. A package already registered at or above the required
version is skipped, and a shared framework already present is not unpacked over. Nothing is ever
removed or downgraded, and nothing is installed on your machine. Each version is published by moving
a fully unpacked staging folder into place, so an interrupted install leaves disposable temporary
content rather than a half-populated runtime.

Afterwards the whole required graph is re-read and verified. If it cannot be satisfied, the command
fails with `sandbox_runtime_provision_failed` naming the requirement that is missing — before the
app is deployed and launched, rather than as an unexplained startup failure:

```text
Windows Sandbox is missing a runtime the app requires: Microsoft.WindowsDesktop.App 10.0.0 or newer.
```

One dependency is fetched rather than found: the desktop VC runtime
(`Microsoft.VCLibs.140.00.UWPDesktop`) ships in no package a build restores and is not in the
Windows SDK, so winapp downloads it from its one official Microsoft address when no host copy
exists. The downloaded bytes are staged, never written straight to the cache, and must clear two
gates before they are published. First the staged file must carry a valid Authenticode signature
that chains to a trusted root and is signed by Microsoft; then its manifest identity, version,
architecture, and publisher must match what was asked for. The signature comes first because
everything the identity check reads lives inside the downloaded package and can be written to say
anything, so identity alone proves only that a package *claims* to be Microsoft's. A failure at
either gate deletes the staged file and publishes nothing, so a rejected payload never becomes a
host cache entry that a later run — or a later guest — would trust without re-deriving it. The
allowlist is a closed list of known packages, not a rule about names. Any other dependency with no
available payload is verified in the guest instead; if the Sandbox already has it, the run proceeds.

The complete graph is verified before **every** launch, not just the first. A clean provisioning
record says what winapp did, not what the guest currently has — `target exec` gives any caller a
way to change package and runtime state inside the same Sandbox generation. Re-verification is
cheap: payloads the guest already holds are not re-transferred, and nothing already satisfied is
reinstalled. The record's job is narrower — it says whether a previous pass was interrupted, and
therefore whether the staged copy has to be rebuilt from scratch.

## Deployment

Each resolved input gets an internal deployment identity derived from its canonical path and, when
present, its original package identity. It scopes guest folders, state, ownership, and artifacts. It
is not a public target and is never guessed from the current directory.

After the host build, winapp captures an immutable snapshot of the desired layout — relative paths,
sizes, timestamps, and content hashes. If files change while the snapshot is being taken, deployment
aborts and asks for a rebuild rather than shipping a mixture of two builds.

A rerun then reconciles exactly: the deployment is marked dirty and the desired state persisted
*before* any file moves, changed and added files are transferred and hash-verified, files absent
from the desired state are deleted, the whole layout is re-compared, and only then is the deployment
marked clean.

Two consequences are worth knowing:

- A file you deleted from your build output is deleted in the guest. Leaving it would let a rerun
  keep executing code you just removed.
- A deployment that was interrupted never launches. The next run redeploys it completely. There is
  no partial-success state that reports healthy.

Package registration preserves per-user application state by default. `run --clean` is the explicit
clean reinstall, and it clears only that deployment's own state.

winapp unregisters only a package whose registered location matches the deployment, and confirms it
is a development-mode registration before removing it. A package you installed yourself is never
adopted or removed, and a provisioned or inbox package that blocks development registration is
reported with its exact identity rather than removed.

## Guest agent

A hidden mode of the architecture-matched `winapp.exe` runs as a persistent agent inside the
Sandbox. It is not a public command.

The agent runs ordinary guest `winapp` child commands for run, debugging, and UI automation.
Package inventory and exact-full-name removal use structured Windows package operations so target
ownership checks cannot widen into name-based removal.

At startup it verifies it is not in session 0 and that its window station and input desktop are
interactive. It publishes its status **whether or not it is ready**, so a disconnected Sandbox
window is reported as exactly that rather than as a timeout.

It then accepts host channels concurrently. Each one completes its own handshake and derives its own
session keys, so channels cannot read, replay, or reorder each other's frames, and each owns its own
operations. Shutting the agent down stops every channel's operations and waits for them, so nothing
it started outlives it.

Every command runs inside a Job Object so that cancelling it terminates the whole process tree, not
just the process winapp started. Because Windows cannot create a process that is already a job
member, the agent starts a small internal barrier instead: it waits until the agent has placed it in
the job, and only then starts the requested command — which Windows puts in the job at creation,
because its parent is already a member. There is no window in which a spawned descendant could
escape its operation's cancellation.

The agent also places *itself* in a job, which is what makes that guarantee hold under any timing:
Windows puts every descendant of a job member into the job at creation, so nothing the agent starts
can escape it. The cost of that is the detached-app limit described in
[Detached apps and the agent's lifetime](#detached-apps-and-the-agents-lifetime) — a process the agent
started does not outlive the agent. Both follow from the same rule, and containment is the half worth
keeping: without it, a cancelled or repaired command could leave guest processes behind holding the
files the next deployment must replace.

The barrier's release signal is per-operation and randomly named, but it is **not a secret**: the
name appears on the barrier process's command line, which any same-user process can read. Randomness
only raises the bar against blind guessing. As with path containment above, a co-resident process
able to exploit that can already terminate the agent outright, so it is accepted under the same
mutually-trusted model rather than defended against.

Host and guest `winapp` are versioned together. When the host is newer, the replacement binary is
staged, hash-verified, self-tested in its own process, and activated only if it passes, with the
previous binary retained as last-known-good. A newer guest is never downgraded: it is reused when
protocol versions overlap, and reported incompatible when they do not — in which case updating the
host is the fix.

## Coordination between commands

There is no background winapp service. Concurrent winapp processes coordinate through per-target
locks, atomic revisioned state files, and a generation identity carried on every request and result.

The guest agent serves several winapp commands at once — up to eight channels — so a running
application and a separate inspection, input, capture, or `target exec` proceed independently. Each
channel is authenticated on its own and is isolated from the others: operation identities, standard
input, cancellation, and failure never cross between them, and losing one channel stops only the
operations that channel started. Past eight channels a command is refused immediately with
`sandbox_agent_busy` and told to retry, rather than left waiting.

The **mutation lock** covers guest state: runtime installation, deployment synchronization, and
package registration. The **connection lock** covers establishing a channel — creating or repairing
the Sandbox and replacing the guest agent — and is released as soon as the channel exists.

Neither lock covers host builds, running applications, or read-only UI Automation. So a long build or
a running app never blocks another workflow, and an inspection never waits behind a deployment.

If a winapp process dies mid-change, the next one treats the abandoned lock as a recovery signal and
reconciles before mutating further.

These locks are unrelated to UI turn coordination. They protect guest environment and deployment
state, not the desktop.

## Failures

Sandbox failures extend the invoking command's existing error contract. Routing through Sandbox
never moves a command's success or error payload between stdout and stderr, and progress messages
never mix into machine-readable output.

```json
{
  "error": {
    "code": "sandbox_setup_requires_restart",
    "message": "The Windows Sandbox feature was enabled and Windows needs a restart to finish.",
    "context": { "setupState": "FeaturePayloadMissing", "featurePayloadPresent": "false" },
    "userAction": "Restart Windows, then run the command again.",
    "example": "winapp run . --on sandbox"
  }
}
```

A `nextCommand` marked `advisory` needs your judgement and is never run automatically.

Infrastructure failures use codes distinct from your application's exit codes, so "winapp could not
run your app" is always distinguishable from "your app failed".

| Code | Meaning |
|---|---|
| `sandbox_unsupported` | This host cannot run Sandbox at all |
| `sandbox_unmanaged_instance` | A running Sandbox could not be prepared, or more than one is running |
| `sandbox_start_failed` | Creating or starting the managed Sandbox failed |
| `sandbox_no_interactive_session` | No interactive guest session |
| `sandbox_input_not_ready` | Input could not be delivered; nothing was reported as delivered |
| `sandbox_terminated` | The Sandbox went away underneath the command |
| `sandbox_agent_incompatible` | The guest agent needs a newer winapp |
| `sandbox_agent_upgrade_failed` | Staging, self-testing, or activating a replacement agent failed |
| `sandbox_agent_busy` | The agent is already serving as many channels or operations as it allows |
| `sandbox_transport_failed` | The command channel could not be established or was lost |
| `sandbox_transfer_interrupted` | A transfer stopped; no destination was published |
| `sandbox_runtime_provision_failed` | A required runtime could not be provisioned |
| `sandbox_deployment_dirty` | The guest copy is incomplete; it will not launch |
| `sandbox_package_conflict` | Another deployment owns this package identity |
| `sandbox_provisioned_package_conflict` | A provisioned package blocks registration |
| `sandbox_target_ambiguous` | The command did not identify exactly one target |
| `sandbox_target_stale` | State refers to a Sandbox that no longer exists |
| `sandbox_stale_handle` | A process ID or window handle from a previous generation |
| `sandbox_artifact_failed` | Producing, verifying, or publishing an output failed |
| `sandbox_setup_requires_elevation` | Enabling the Sandbox feature needs permission you declined |
| `sandbox_setup_requires_restart` | The feature is enabled; Windows needs a restart |
| `sandbox_setup_failed` | Windows refused to enable the feature or start the client |
| `sandbox_setup_incomplete` | Windows is still installing the client; retrying resumes it |

Two more codes are target-neutral, because they are raised before any provider is chosen:
`target_invalid` (the named target is not one winapp knows) and `target_invalid_arguments` (the
command line itself was rejected). A `target` command run with `--json` reports even a mistyped option
as the same JSON error envelope on stderr, so a caller parsing JSON never has to parse help text.

## Architecture
Windows Sandbox is the only public target. Internally it sits behind a narrow boundary so a future
Hyper-V, Dev Box, or remote-machine target can reuse everything above it without a rewrite.

```text
run / ui / unregister / target exec / target push / target snapshot / target screenshot / target record
        │
        ▼
ExecutionTargetOrchestrator      probe → connect (locked) → negotiate → lock (only if mutating)
        │
        ├── TargetDeploymentService     snapshot, reconcile, ownership
        │
        ▼
GuestCommandChannel              one target-neutral protocol
        │  IGuestTransport
        ▼
WindowsSandboxBackend            the only code that knows Sandbox exists
        │  wsb start / list / share / connect / exec / stop
        ▼
Windows Sandbox  ──►  guest winapp agent  ──►  guest winapp child commands
```

Only the backend may invoke `wsb.exe` or touch the Sandbox window. Deployment, runtime provisioning,
UI forwarding, and artifact handling sit above the boundary and never reference Sandbox APIs, paths,
or IDs. That separation is enforced by tests rather than convention: the orchestration test suite
runs the real host channel against the real guest server over an in-memory transport, so any
dependency on a `wsb` command would make those tests impossible to run.

Host-side capture keeps the same boundary. Which window on this machine renders a guest desktop is a
fact only a provider can know, so the backend answers it and the orchestrator exposes one narrow
result — a window handle and the process behind it. `target screenshot` and `target record` consume
that and then reuse the same host screenshot and recording services `winapp ui` uses; they contain no
Sandbox knowledge and no capture code of their own. A target that renders nowhere on this machine
simply does not answer, and the verbs report that instead of failing obscurely.

`wsb exec` is used for exactly two things — launching the agent and enabling the guest development
prerequisite — because it takes the command as a
single string and returns only an exit code. It can carry neither argument boundaries nor guest
output, which is why real work goes over the authenticated channel and why the agent writes its own
startup diagnostics to a bounded, guest-writable folder the host reads once and removes.

### Telemetry

`winapp target exec sandbox` and `winapp target push sandbox` never contribute executable or argument values,
environment variables, host or guest paths, stream contents, or file names to telemetry. String,
file, and directory values are recorded as a constant placeholder rather than their content, and
tests pin that so a future change to the redaction rule cannot silently start collecting them.

### Live coverage

Almost everything is verified without a Sandbox: the real host channel runs against the real guest
server over an in-memory transport, so a dependency on a `wsb` command would make those tests
impossible to run at all. The tests that do need a real machine are gated on
`WINAPP_SANDBOX_E2E=1` and `WINAPP_SANDBOX_E2E_BINARY`, which must point to the
architecture-matched NativeAOT `winapp.exe` produced under `artifacts\cli\win-x64` or
`artifacts\cli\win-arm64`. Windows permits one Sandbox at a time and creating one is a machine-wide,
visible side effect. The tests stop only an instance they created, and skip rather than fail when an
unowned one is already running.

## See also

- [UI automation](ui-automation.md)
- [Debugging with package identity](debugging.md)
- [Security guidance](security.md)
