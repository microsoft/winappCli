<!-- mslearn: true -->
<!-- ms.topic: concept-article -->
<!-- description: Run, debug, and UI-automate Windows applications inside a persistent Windows Sandbox using the winapp CLI --on sandbox option. -->
# Windows Sandbox execution

Build on your machine, then run and automate the app in Windows Sandbox:

```powershell
winapp run . --on sandbox --detach
winapp ui inspect --on sandbox -a MyApp
winapp ui invoke --on sandbox SubmitButton -a MyApp
```

Replace `MyApp` with your app name or the guest PID printed by `run`. `--detach` returns
after launch so the next command can inspect the app; without it, `run` waits for the
app to exit. The Sandbox stays running between commands and rebuilds.

> [!NOTE]
> Windows Sandbox execution is in development and is not available in a released build yet.

## Before you start

- Use Windows 11 24H2 or newer on a supported edition, with hardware virtualization enabled.
- Guest winapp supports x64 and Arm64. An x86 app requires guest support for running it
  and matching x86 dependencies; an x64 runtime does not satisfy an x86 app.
- Keep the host session unlocked for real input and screen capture.

Requesting `--on sandbox` allows winapp to enable the Windows Sandbox optional feature
and install the Sandbox client if needed. Windows may ask for administrator permission.
If a restart is required, winapp stops and tells you; **you decide when to restart**.
Setup can take several minutes. If it reports `sandbox_setup_incomplete`, wait and retry
the command to resume installation.

Setup, a cold connection, or a reconnect can briefly take focus. Once connected, winapp
keeps its own client window off-screen without activating it. A Sandbox window you
opened yourself is left in place.

> [!IMPORTANT]
> **Builds still run on your machine.** Project evaluation, restore, and compilation are
> not isolated. `--on sandbox` does not make an untrusted project safe to build.
>
> **One Sandbox is one shared environment.** Apps and workflows inside it share the user,
> desktop, registry, packages, runtimes, and network access. They can observe or interfere
> with each other. Use separate machines for mutually untrusted workflows.

Windows permits one Sandbox at a time. winapp reuses a running instance, including one
you opened yourself. Preparing it adds winapp's shared bootstrap folders, guest agent,
Developer Mode, and an inbound firewall rule. winapp does not stop an adopted instance
or remove unrelated apps. There is **no silent host fallback**: a command requesting
Sandbox runs there or fails.

## Running and rebuilding

```powershell
winapp run .\MyApp.csproj --on sandbox --detach --json
winapp run .\publish --on sandbox --detach
winapp run . --on sandbox --clean --detach
```

Build options such as `--configuration`, `--arch`, `--framework`, `--property`,
`--no-build`, and `--no-restore` apply on the host. Registration, launch, and debugging
happen in the guest; the app is not registered on your machine.

| Option | Effect in Sandbox |
|---|---|
| `--detach` | Return after launch rather than wait for exit |
| `--no-launch` | Deploy and register without launching |
| `--clean` | Reinstall this deployment and clear its application data |
| `--unregister-on-exit` | Remove this package registration after the app exits |
| `--with-alias` | Launch its guest execution alias with forwarded streams |
| `--debug-output` | Stream guest debug output; **packaged apps only** |

Unpackaged apps launch their executable from the deployed folder. They have no package
to register. `--debug-output` is rejected for unpackaged Sandbox runs.

Rerunning transfers changed files and removes files deleted from the build output.
Application data is preserved unless you request `--clean`. An incomplete deployment
does not launch; retrying rebuilds its guest copy. If build files change while winapp is
preparing them, finish the build and retry.

Warm UI commands report only their result, without repeating a Sandbox preparation
message. Initial setup and recovery still report progress. Use `--verbose` for
connection timings and diagnostic details; `--quiet` and `--json` suppress progress.
JSON runs include a guest process ID and target scope:

```json
{
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

These are additional fields in the run result, not a separate document. Copy the whole
`UiTargetArgs` value when inspecting the app: `winapp ui inspect --on sandbox -a 4212`.
Rediscover PIDs and window handles after the Sandbox is recreated; they belong to that
Sandbox generation, not the host or a future guest.

### Detached apps and the agent's lifetime

A detached **unpackaged** app ends if the guest agent stops, including during agent repair.
If it disappears between commands, rerun with `--detach` and rediscover its UI target.
Waiting for the app rather than detaching lets you observe its exit; it does not make the
app survive agent loss. Packaged apps use Windows activation rather than the agent's
process lifetime. Closing or restarting the Sandbox ends all apps inside it.

### Shared runtimes

winapp checks the app's package dependencies, Windows App SDK requirements, and
`*.runtimeconfig.json` before launch. It uses host caches or downloads needed payloads,
then installs missing supported runtimes **in the guest**, not on your machine.

Package requirements include publisher, version, and architecture. Shared .NET runtime
selection honors the application's configured roll-forward policy and architecture;
do not assume any newer runtime in the same major version will work.

If a framework, runtime configuration, or dependency cannot be supported, the command
fails explicitly before launch and identifies the requirement. Follow that error's
action. Where supported by your project, publishing self-contained removes the need
for the corresponding shared runtime; it does not remove unrelated package dependencies.

## Automating the UI

```powershell
winapp ui list-windows --on sandbox
winapp ui inspect --on sandbox -a MyApp
winapp ui invoke --on sandbox SubmitButton -a MyApp
winapp ui screenshot --on sandbox -a MyApp -o .\result.png
```

Every `ui` verb accepts `--on sandbox`. App names, PIDs, window handles, and selectors
are resolved inside the guest. Use `-a/--app` or `-w/--window` for app-targeted commands;
winapp does not guess the last app launched. Omitting `--on sandbox` selects your host
desktop instead.

Real input and recording require a **connected, nonminimized Sandbox client**. Read-only
inspection can still work when input cannot. winapp can restore its own minimized
client without activation; a minimized manually opened client must be restored by you.
If input is unavailable after reconnecting, the command fails rather than claiming
it delivered input. Use the reconnect command in the error and retry.

See [UI automation](ui-automation.md) for selectors, input methods, and assertions.

### Coordinating UI workflows in the Sandbox

Use one `WINAPP_UI_WORKFLOW_ID` for cooperating commands, and a different value for
each independent workflow. Set it on **every invocation**, especially when your agent
starts a fresh shell for each tool call. winapp forwards a hashed, Sandbox-generation
specific identity; the raw host value is not sent to the guest.

For example, record and interact in two terminals using the same value. Choose a new
value for each new workflow.

Terminal 1:

```powershell
$env:WINAPP_UI_WORKFLOW_ID = 'myapp-checkout-01'
winapp ui record --on sandbox -a MyApp --duration-sec 20 --frames -o .\checkout.mp4
```

Terminal 2, while the recording is running:

```powershell
$env:WINAPP_UI_WORKFLOW_ID = 'myapp-checkout-01'
winapp ui invoke --on sandbox SubmitButton -a MyApp
$env:WINAPP_UI_WORKFLOW_ID = 'myapp-checkout-01'
winapp ui inspect --on sandbox -a MyApp
```

Once **both** the recording and actions have finished:

```powershell
$env:WINAPP_UI_WORKFLOW_ID = 'myapp-checkout-01'
winapp ui yield --on sandbox
```

A named workflow retains its UI turn for four seconds after its last command; `yield`
releases it immediately. Without an ID, each command releases its turn on completion.
A no-ID recording therefore blocks other desktop-changing workflows for its duration.
Read-only inspection does not wait. Host and guest UI turns are separate.

After a pause, inspect again and reopen any menu or dialog you need: another workflow
may have used the guest desktop. Cooperative turns do not isolate apps from each other.

## Screenshots and recordings

Use `ui` capture for an app's window, or `target` capture for the **whole native guest
desktop**, including the shell and installer dialogs:

```powershell
winapp ui record --on sandbox -a MyApp --duration-sec 10 --frames -o .\app.mp4
winapp target screenshot sandbox -o .\sandbox.png
winapp target record sandbox --duration-sec 20 --frames -o .\sandbox.mp4
```

Outputs land on the **host**, including when you omit `-o`. Screenshots default to
`screenshot.png`; recordings use `recording-<timestamp>-<guid>.mp4`.
For recordings, `--frames` also delivers the `<output-name>.frames` directory containing
JPEGs, `frames.ndjson`, and `manifest.json`. Results report host paths. Target recordings
run in the guest; their host files become available after recording finishes and delivery completes.

`target screenshot` excludes the host Sandbox window's title bar and borders. Its PNG is
unscaled: with guest screen origin `(0,0)`, image coordinates are directly usable by
coordinate-input verbs such as `ui drag` or `ui touch --at`, with `--on sandbox`.
Add the reported origin for a desktop with a negative origin.
Use `--json` to read `coordinates.sourceBounds` and `coordinates.contentRect`; both use
physical pixels and exclusive right/bottom edges.

Target recordings report the same fields in JSON and the frame manifest. MP4 and JPEG
frames share the mapping, including `--max-edge` scaling and encoder padding. To map
image pixel `(x,y)`, first reject points outside `contentRect`, then compute each source
coordinate as `sourceStart + floor((pixel - contentStart + 0.5) * sourceSize / contentSize)`.
Downscaling loses precision; use a native PNG when exact coordinates matter. A change
to the guest desktop's bounds stops the recording with `display_changed`, preserving
only frames from before the change and marking the frame manifest partial.

An existing MP4 or paired `.frames` directory is rejected by default. Use a new path, or pass
`--overwrite` to replace them after the new take finishes. Previous frame bundles are
retained as `<output-name>.frames.previous-<id>`, including when the replacement omits
`--frames`. A failed capture leaves the old recording intact.

Prefer a positive `--duration-sec` for scripts and agents. The npm `uiRecord` and
`targetRecord` helpers require `durationSec`; their abort signal cancels forcefully,
not as a graceful stop. See [`ui record`](ui-automation.md#record) for supported values.
Without a CLI duration, recording waits for a stop signal.

Ctrl+C after capture starts can finalize and return a recording successfully with
`stopReason: cancelled`. Other interruptions can preserve useful video or frames. Read `stopReason`,
`partialOutput`, and `recoveryHint` when present, and use the reported evidence paths
rather than assuming a normal completion. If the whole-desktop capture becomes
unavailable during a take, it stops with `capture_unavailable` rather than continuing
to capture an unavailable desktop. It does not bring the Sandbox to the foreground to rescue a
frame. Capture can fail before any usable evidence is available.

For a failed guest recording, recovered evidence is placed in a unique
`<output>.partial-<id>` directory on the host. If delivery fails, received files remain
under the reported recovery path, such as `<output>.recovery-<id>`, and guest originals
are retained. Keep the Sandbox running and follow the error's recovery action before
retrying or closing it. A preserved partial file is not necessarily a playable video.

Screenshots and video may contain sensitive information. Handle the frame directory
with the same care as the MP4. See [`ui record`](ui-automation.md#record) for recording
options and result fields.

## Inspecting the Sandbox

```powershell
winapp target snapshot sandbox
winapp target snapshot sandbox --json
```

This reports readiness, current deployments, and guest windows without creating a VM,
reconnecting the client, or repairing the agent. With no Sandbox running, it reports
that fact and exits successfully. To start one, use `winapp run . --on sandbox --detach`.

The report distinguishes what the guest supports from what the current client can do;
a minimized client can prevent input or capture even when the guest supports both.
Use the guest window list for UI PIDs, not a deployment's tracked launcher process.
If several client windows prevent an unambiguous capture, the error lists candidates;
decide which to close before retrying.

## Running commands and copying files

```powershell
winapp target exec sandbox -- dotnet --info
winapp target push sandbox .\setup.ps1 Setup\setup.ps1
winapp target exec sandbox --cwd C:\WinApp\work\Setup -- powershell -ExecutionPolicy Bypass -File .\setup.ps1
winapp target pull sandbox Results .\results
```

Use `target exec` for setup and diagnostics. It runs as the guest user, forwards
standard streams, and returns the command's exit code. It is not a full interactive
terminal; console applications see redirected pipes. `--json` formats winapp errors,
not the child command's stdout.

For `push` and `pull`, **target paths are relative to `C:\WinApp\work`**. Absolute,
rooted, and UNC target paths are refused. A single file lands at exactly the destination
you name; a directory preserves its structure beneath that destination. Use the
resolved guest path printed after a push as the next command's `--cwd`.

Only run setup scripts you trust. The example uses process-scoped
`-ExecutionPolicy Bypass` because a fresh Sandbox normally refuses scripts under its
`Restricted` policy.

Transfers skip unchanged files and verify replacements before publishing them.
Symbolic links and junctions are not followed: deployment rejects them, while directory
copies skip linked entries. A directly named linked source or a destination path through
a link is refused. Copy the real files or directories instead.

## Removing an app and ending the Sandbox

```powershell
winapp unregister --on sandbox --manifest .\Package.appxmanifest
```

With a manifest in the current directory, you can omit `--manifest`. This removes only
the matching development package registered by winapp in the current Sandbox.
An externally installed package is left alone, even if its identity matches.
`--force` is not supported with `--on`; it cannot bypass ownership checks.
This is manifest-based package cleanup, not an unregister command for unpackaged apps
or a `.cs` input.

The Sandbox remains running. Manage its lifetime with Windows Sandbox's own CLI:

```powershell
wsb list
wsb connect --id <id>
wsb stop --id <id>
```

Stopping discards the guest and its work. Save needed evidence first, and obtain the
user's consent before stopping an instance they may be using. Later winapp commands
can create a fresh Sandbox; rediscover all app targets afterwards.

## Troubleshooting

Follow the error's `userAction`; an advisory `nextCommand` is a suggestion, not permission
to run it automatically. In automation, inspect the structured `error.code`.
Infrastructure failures may exit `70`, but an arbitrary application can also return
`70`; the numeric exit status alone does not distinguish them.

Recovery commands suggested by routed UI operations retain `--on <target>`, so
copying a suggestion keeps it on the same execution target.

| Error or symptom | What to do |
|---|---|
| `sandbox_unsupported`, `sandbox_setup_failed` | Check Windows edition/version, firmware virtualization, and optional-feature policy |
| `sandbox_setup_requires_elevation` | Let the user approve elevation, or run the error's setup command from an approved elevated terminal |
| `sandbox_setup_requires_restart` | Let the user choose when to restart, then retry |
| `sandbox_setup_incomplete` | Wait for Windows installation to finish, then retry |
| `sandbox_unmanaged_instance`, `sandbox_target_ambiguous` | Inspect the reported instances/windows; do not stop unrelated work to resolve ambiguity |
| `sandbox_input_not_ready`, `sandbox_no_interactive_session` | Restore the existing client or reconnect as directed, then retry |
| `sandbox_agent_incompatible` | Follow the version error; upgrade the installed CLI using its installation method if requested, then close/retry only with consent |
| `sandbox_agent_busy` | Wait for another command to finish, then retry |
| `sandbox_terminated`, `sandbox_target_stale`, `sandbox_stale_handle` | Rerun the app and rediscover guest PIDs/windows |
| `sandbox_deployment_dirty`, `sandbox_transfer_interrupted` | Retry the deployment or transfer |
| `sandbox_runtime_provision_failed` | Resolve the named dependency or unsupported runtime configuration; see [Shared runtimes](#shared-runtimes) |
| `sandbox_package_conflict`, `sandbox_provisioned_package_conflict` | Follow the package-specific action; do not remove unrelated or inbox packages |
| `sandbox_artifact_failed` | Check the reported output and client readiness; preserve any partial evidence |
| `target_invalid`, `target_invalid_arguments` | Correct the target or options shown in the error |

`winapp update` updates **project SDK dependencies**, not the installed CLI. It is not
a fix for host/guest CLI incompatibility.

### Share targets in the build 28000 Sandbox

The tested build 28000 Sandbox cannot enumerate Share targets. Test other app features
in Sandbox, but validate Share source-to-target flows outside it.

## See also

- [Command syntax](usage.md#target)
- [UI automation](ui-automation.md)
- [Debugging with package identity](debugging.md)
- [Security guidance](security.md)
