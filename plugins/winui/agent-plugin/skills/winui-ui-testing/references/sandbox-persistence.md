# Guest persistence and file transfer

### Guest persistence and arbitrary file transfer

Do not inspect **host** `$env:LOCALAPPDATA` to prove **guest** persistence. Check the actual app data file in the selected target. Replace the sample package family/path with the app's real storage location; unpackaged apps usually use an app-specific LocalAppData directory.

```powershell
Test-UI 'Username persisted in selected target' {
    $readSettings = @'
$ErrorActionPreference = 'Stop'
$file = Join-Path $env:LOCALAPPDATA 'Packages\YourPackageFamily\LocalState\settings.json'
if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing settings: $file" }
Get-Content -LiteralPath $file -Raw
'@
    $json = if ($Target -eq 'sandbox') {
        Invoke-WinAppChecked @('target', 'exec', 'sandbox', '--', 'powershell.exe', '-NoProfile', '-NonInteractive', '-Command', $readSettings)
    } else {
        & ([scriptblock]::Create($readSettings))
    }
    $settings = $json | ConvertFrom-Json
    if ($settings.UserName -ne 'TestUser') { throw 'Saved username does not match.' }
}
```

For arbitrary setup/results files, use `target exec` / `push` / `pull`, not host filesystem guesses. Transfer guest paths are **relative to snapshot `workRoot`**, normally `C:\WinApp\work`, not absolute guest paths. The following optional additions assume a host `setup.ps1` that creates `Results` under the guest work root:

```powershell
Test-UI 'Guest setup and result transfer' {
    if ($Target -ne 'sandbox') { throw 'This transfer test requires Sandbox.' }
    $snapshot = Invoke-WinAppChecked @('target', 'snapshot', 'sandbox', '--json') | ConvertFrom-Json
    if (-not $snapshot.workRoot) { throw 'No live guest workRoot; fix readiness first.' }
    Invoke-WinAppChecked @('target', 'push', 'sandbox', '.\setup.ps1', 'Setup\setup.ps1')
    $guestSetup = $snapshot.workRoot.TrimEnd('\') + '\Setup\setup.ps1'
    Invoke-WinAppChecked @('target', 'exec', 'sandbox', '--', 'powershell.exe', '-NoProfile', '-File', $guestSetup)
    Invoke-WinAppChecked @('target', 'pull', 'sandbox', 'Results', (Join-Path $ArtifactDirectory 'results'))
}
```

Keep the guest alive while recovering failed delivery. Preserve evidence/recovery paths and app data needed for diagnosis before any **user-consented** shutdown; never stop all Sandboxes as routine cleanup.
