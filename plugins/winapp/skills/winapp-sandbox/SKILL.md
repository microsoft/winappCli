---
name: winapp-sandbox
description: Run, debug, and UI-automate a Windows app in a persistent Windows Sandbox rather than the user's desktop. Use for disposable app testing, guest diagnostics, file transfer, and app or whole-desktop evidence. Builds remain on the host; connection and reconnect can briefly take focus.
---
## Before acting

- Confirm the user wants Sandbox execution. Do not drop `--on sandbox` to bypass an error.
- Builds, project evaluation, and restore still run on the host. Do not build untrusted
  projects on the assumption that Sandbox isolates them.
- Windows supports one Sandbox; all apps inside it share a user, desktop, and state.
  Do not treat separate workflows as mutually isolated.
- Windows 11 24H2+ on a supported edition and hardware virtualization are required.
  Guest winapp supports x64/Arm64; x86 apps need guest support and matching dependencies.
- winapp only checks host prerequisites: it does not enable features, install the
  client, request elevation, or reboot. `--on sandbox` selects the target, not setup consent.
- On `sandbox_setup_required`, explain the required Windows feature and restart.
  Decide whether to offer agent-assisted setup or give the user manual steps; obtain
  explicit approval before running the suggested command in an elevated terminal.
  Use `/NoRestart` and let the user choose when to reboot:
  `dism.exe /Online /Enable-Feature /FeatureName:Containers-DisposableClientVM /All /NoRestart`.
- On `sandbox_setup_requires_restart`, ask the user to save work and restart when
  ready. Never restart automatically or treat setup approval as reboot approval.
- On `sandbox_setup_incomplete`, direct the user to open Windows Sandbox from Start
  and finish its client setup/update. Do not repeatedly retry an unchanged prerequisite.
- Connection or reconnect may briefly take focus; do not promise zero desktop interruption.
- Existing Sandbox instances are reused and changed, not discarded. Never close one
  or run `wsb stop` without user consent.

## Launch, inspect, act, verify

```powershell
winapp run . --on sandbox --detach
winapp ui list-windows --on sandbox
winapp ui inspect --on sandbox -a MyApp
winapp ui invoke --on sandbox SubmitButton -a MyApp
winapp ui screenshot --on sandbox -a MyApp -o .\result.png
```

1. Use `--detach` when subsequent commands must inspect the running app; without it,
   `run` waits for exit. Use `--json` when consuming the launch result programmatically.
2. Replace `MyApp` with the discovered name or copy the returned `UiTargetArgs`.
   Keep `--on sandbox` on every guest UI command, including commands using a PID/HWND.
3. Inspect before acting and verify the result. Use `winapp-ui-automation` for selectors
   and input methods. Real input and recording need a connected, nonminimized client;
   read-only inspection can still work when input cannot.
4. Rebuild by rerunning the same command. Add `--clean` only when clearing that app's
   data is intended. `--debug-output` is supported only for packaged Sandbox apps.
5. Rediscover targets after the Sandbox is recreated. A detached unpackaged app can
   also end during guest-agent repair; rerun it if it disappears.

## Coordinate a recording with actions

Choose one explicit workflow ID and inject the **same value into every cooperating
invocation**, including fresh tool-call shells. Different workflows need different IDs.

Recording invocation:

```powershell
$env:WINAPP_UI_WORKFLOW_ID = 'myapp-checkout-01'
winapp ui record --on sandbox -a MyApp --duration-sec 20 --frames -o .\checkout.mp4
```

While it records, in another invocation:

```powershell
$env:WINAPP_UI_WORKFLOW_ID = 'myapp-checkout-01'
winapp ui invoke --on sandbox SubmitButton -a MyApp
```

After both recording and actions finish:

```powershell
$env:WINAPP_UI_WORKFLOW_ID = 'myapp-checkout-01'
winapp ui yield --on sandbox
```

The forwarded identity is hashed and scoped to this Sandbox generation. Without an ID,
each command is a one-shot that releases immediately; a no-ID recording blocks other
desktop-changing workflows for its duration. An explicit ID keeps a four-second idle
grace. Yield at the end instead of making another workflow wait. After a reasoning gap,
inspect again and reopen transient UI before acting.

## Choose evidence by scope

```powershell
winapp target snapshot sandbox
winapp target screenshot sandbox -o .\sandbox.png
winapp target record sandbox --duration-sec 20 --frames -o .\sandbox.mp4
```

- Start with `target snapshot` when an app never appeared or a command failed. It does
  not create a VM, reconnect the client, or repair an agent.
- `target screenshot`/`target record` capture the native guest desktop, not the host client window.
  `ui screenshot`/`ui record --on sandbox` capture an app window.
- Outputs, including default filenames when `-o` is omitted, are delivered to the host.
  `--frames` returns a frame directory alongside the MP4 after recording finishes.
- Use the PNG's native coordinates plus its reported screen origin for guest input.
  For scaled recording frames, use `coordinates.sourceBounds` and `coordinates.contentRect`
  from the JSON/manifest, not raw image coordinates. The mapping is documented in
  [Sandbox capture](../../../../docs/sandbox-execution.md#screenshots-and-recordings).
  `display_changed` means capture stopped before the desktop bounds changed its mapping.
- Prefer a positive `--duration-sec` for unattended CLI recording. npm helpers require
  `durationSec` (integer 1–86400); abort signals cancel forcefully, not gracefully.
- Choose a fresh output path. Request `--overwrite` only when replacement is intended:
  it replaces the MP4 after the new take finishes and archives any previous frame
  directory. Do not delete partial evidence to make a retry pass.
- Read `stopReason`, preserved paths, and `recoveryHint` before reporting success.
  A `capture_unavailable` stop can leave useful evidence but is not a full take.
  Ctrl+C can finalize with `stopReason: cancelled` and a successful exit.
- If delivery fails, keep the Sandbox running and follow the error's recovery action.
  Preserve both the received host files and guest originals until recovery finishes.
- Treat frames, screenshots, and video as potentially sensitive.

## Guest setup and file transfer

```powershell
winapp target push sandbox .\setup.ps1 Setup\setup.ps1
winapp target exec sandbox --cwd C:\WinApp\work\Setup -- powershell -ExecutionPolicy Bypass -File .\setup.ps1
winapp target pull sandbox Results .\results
```

Use `target exec` only for necessary setup or diagnostics, not instead of `winapp run`.
It streams the command's output and is not a full terminal. The example's execution-policy
override is scoped to that PowerShell process; run only a trusted script.

Push/pull target paths are **relative to `C:\WinApp\work`**; absolute, rooted, and UNC
paths are rejected. A single-file destination includes the filename. Use the reported
resolved guest path for `--cwd`. Directory copies skip linked entries; directly named
linked sources and paths through destination links are rejected. Deployment rejects links.

## Cleanup and recovery

```powershell
winapp unregister --on sandbox --manifest .\Package.appxmanifest
```

This removes only the matching winapp-owned development registration. It needs a
manifest, does not support `--force`, and does not stop the Sandbox. Do not suggest
`.cs` input cleanup through target unregister.

Follow the error's `userAction`, not just its exit number: infrastructure failures and
an application's own exit can both be `70`. Human setup progress goes to stderr and is
suppressed with `--quiet`/`--json`.

- Prerequisite errors: follow the setup guidance above; keep elevation and restart under user control.
- Input unavailable: restore the existing client or use the error's reconnect command.
- Incompatible CLI: follow the error; upgrade the installed CLI through its install method,
  **not `winapp update`**. Obtain consent before closing a Sandbox for a version change.
- Missing/unsupported runtime: use the named requirement and configuration in the error.
  Do not assume any newer same-major runtime is compatible or substitute architectures.
- Incomplete deployment/transfer: retry. Busy: wait. Partial recording: keep reported evidence.
- Package conflict: do not remove external or inbox packages to force registration.

See [Windows Sandbox execution](https://github.com/microsoft/WinAppCli/blob/main/docs/sandbox-execution.md)
for prerequisites, runtime support, lifecycle, output recovery, and known limitations.
