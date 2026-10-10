---
name: winui-ui-testing
description: "Write and run automated UI tests for Windows desktop apps (WinUI 3, WPF, WinForms, Win32) with the winapp ui harness: one batch script, assertions, input, file pickers, dialogs, screenshots, locally or in Windows Sandbox. Use when asked for UI tests or to verify a feature end to end."
---

### Scope and execution boundary

`winapp ui` uses Windows UI Automation (UIA), so the AutomationId-based workflow works with any Windows desktop framework, packaged or unpackaged. Skip WinUI-specific binding/dialog advice for other frameworks.

Windows Sandbox keeps synthetic input off the user's desktop; target selection is in Step 1. Discover the installed contract with `winapp run --help`, `winapp target --help`, and `winapp ui <verb> --help`. Use `--on sandbox`, not `--sandbox` or a top-level `sandbox` command. Do not enable Windows features or launch an app without the task's permission.

- Prerequisites and enablement: see `winui-setup`.
- Project builds/publishes execute on the **host**; deployment, app launch, and `ui --on sandbox` execute in the **guest**. Run the batch script below on the host, not inside `target exec` (which would double-route).
- Real input and capture require an unlocked host and a connected, nonminimized Sandbox client. Tree inspection may work while input cannot; a readable tree is not an input-readiness check.
- `winapp target snapshot sandbox --json` is a read-only readiness query: it neither starts nor reconnects a guest. Use it to diagnose readiness rather than probing the user's desktop.
- The persistent guest is shared, not isolation between mutually untrusted workflows. Coordinate with other users; there is no supported `--unique-identity` option. Use separate machines for mutually untrusted work.
- Guest `--debug-output` supports **packaged** apps only.
- Never stop all Sandboxes as routine cleanup. Shut a guest down only with the user's consent, after preserving evidence and app data needed for diagnosis.

### Approach and command discovery

Prefer a single scripted batch over a long series of interactive calls. If you wrote the app, use its XAML/source AutomationIds directly; otherwise inspect its current tree and read source for hidden/lazy flyouts and dialogs. Either way, validate a real window/tree before passing an accessibility audit.

Core verbs: `list-windows`, `inspect`, `search`, `get-property`, `get-value`, `wait-for`; `invoke`, `click`, `set-value`, `focus`, `scroll-into-view`; `send-keys`, `hover`, `drag`, `touch`, `pen`; `screenshot`, `record`. Use each verb's `--help` for selectors and options, rather than guessing from this short reference.

### Step 1: Select a target, then keep the PID and target together

Prefer `sandbox` when Windows Sandbox is available; otherwise tell the user and choose `local`. **If the user explicitly requested Windows Sandbox and it is unavailable, stop** and point them to the enablement steps in `winui-setup`. A stopped guest does not prove unavailability: `target snapshot` can report no running target while the feature is enabled. App build/test failures are not Sandbox unavailability. The same policy applies to diagnostics: unpackaged apps can't use guest `--debug-output`, so diagnose them locally unless Sandbox was explicitly requested.

Pass the selected target to the template: it launches with `winapp run . --on sandbox --detach --json` for a guest, or omits `--on sandbox` for local execution. Reuse an already-running app only when its captured target matches the selected target and the guest has not been recreated. Never pass a guest PID to default-host `winapp ui`. If a target becomes unavailable after selection, report it and select again under the same policy; the script itself never retries in another target.

The run JSON includes **`ProcessId`**, **`ProcessScope`**, and **`UiTargetArgs`** (PascalCase). In Windows Sandbox, `UiTargetArgs` is **`--on sandbox -a <guestPID>`**: preserve both parts, not just the number. For an explicitly requested **unpackaged** guest run, the launch result has no app PID: launch separately, find the intended app by process/title with target-wide `winapp ui list-windows --on sandbox --json`, then pass its PID with `-Target sandbox -AppPid <PID> -AppScope sandbox`. Never use the containment PID. A fresh guest invalidates old PIDs/HWNDs.

### Step 2: Write the batch script

Read `references/batch-template.md` and create `ui-tests.ps1` from its template.

### Step 3: Run, read, and visually verify

```powershell
# Preferred when Windows Sandbox is available:
.\ui-tests.ps1 -Target sandbox
if ($LASTEXITCODE -ne 0) { throw 'UI batch failed; read test-results.json.' }
$report = Get-Content -LiteralPath '.\test-results.json' -Raw | ConvertFrom-Json
$report.screenshots
```

For a captured, still-live guest PID, use `.\ui-tests.ps1 -Target sandbox -AppPid 1234 -AppScope sandbox`. For a local run (see Step 1), use `.\ui-tests.ps1 -Target local`, optionally with a local PID and `-AppScope local`. Never reuse a guest PID locally or switch targets to make failing tests pass.

View **each host PNG** listed in `test-results.json` with the image-viewing tool. UIA PASS cannot detect clipping, overlap, incorrect theming, or content bleeding past its container. Fail visual review for unintended scrollbars, unintended ellipses, clipped hero/right-edge controls, overlapping rows, unbalanced whitespace, cramped/vast spacing, incorrect Light/Dark/High Contrast, or missing focus/hover/error states. Capture meaningful states immediately after their interactions, not just at the end.

If fixes are requested, batch-fix failures, rebuild/relaunch with the same target using the template (omit `-AppPid` to launch again), and capture the new PID. Maximum **two** fix-and-rerun cycles; then report remaining failures. Do not declare completion without the structured results **and** visual review.

### Assertions and coverage

Write tests for every requested requirement. Use `wait-for --value` for TextBox/NumberBox values, RichEditBox text, ComboBox selection, toggle/checkbox `On`/`Off`, and TextBlock/label text. `--contains` matches a substring; `--gone` waits for disappearance. Use `-p IsEnabled --value True` only for a specific property. Invoke buttons/nav items, wait for page-specific elements, and test default/error/empty states.

The following examples are additions to the template, not independent shells. `Invoke-Ui` always supplies the chosen scope plus `-a $AppPid`, or scope plus `-w $Window`. For raw reads, pass `--json` and parse it; plain stdout can include advisory messages. Use `scroll-into-view` before asserting a virtualized item below the fold.

```powershell
Test-UI 'Status and enabled state' {
    Invoke-Ui @('wait-for', 'StatusBar', '--value', 'saved', '--contains', '-t', '3000')
    Invoke-Ui @('wait-for', 'BtnSave', '-p', 'IsEnabled', '--value', 'True', '-t', '3000')
    $value = Invoke-Ui @('get-value', 'TxtUserName', '--json') | ConvertFrom-Json
    if ($value.text -ne 'TestUser') { throw 'Wrong username.' }
}
```

### Binding and selector gotchas

- `set-value` does not commit default WinUI `x:Bind TwoWay` TextBox bindings until focus changes. Prefer `UpdateSourceTrigger=PropertyChanged` in the app; otherwise `invoke` Save or `focus` another control before checking persistence.
- RichEditBox/RichTextBox generally need `send-keys --via send-input`, not UIA `set-value`.
- Use `$AppPid`, never PowerShell's read-only automatic `$Pid`.
- A picker HWND needs `-w` **and the same target scope**. A fresh guest invalidates both HWNDs and PIDs; do not recycle either.

## Load when

| Read | When |
|---|---|
| `references/batch-template.md` | Writing the test batch script (always, before Step 3) |
| `references/advanced-interactions.md` | Testing file pickers, flyouts, ContentDialogs, keyboard/hover/drag/touch/pen, or recording |
| `references/sandbox-persistence.md` | Tests in Windows Sandbox need persisted data or file transfer |

## Related skills

- `winui-setup` — prerequisites
- `winui-dev-workflow` — build and run
