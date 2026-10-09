# Pickers, flyouts, dialogs, input, and recordings

### File pickers, flyouts, and ContentDialogs

Pickers run in a separate `PickerHost` process. Discover their HWND in the **same target**; `-w` changes the window selector, never the target. Replace the filename with a file that exists **in the guest** for Sandbox testing.

```powershell
Test-UI 'Open file picker' {
    Invoke-Ui @('invoke', 'BtnOpenFile')
    Start-Sleep -Seconds 1
    $allWindows = @(Invoke-WinAppChecked (@('ui', 'list-windows', '--json') + $ScopeArgs) | ConvertFrom-Json)
    $pickers = @($allWindows | Where-Object { $_.hwnd -and [string]$_.ownerHwnd -eq $hwnd -and $_.title -match 'Open|Save' })
    if ($pickers.Count -ne 1) { throw 'Expected one picker; inspect the selected target again.' }
    $pickerHwnd = [string]$pickers[0].hwnd
    Invoke-Ui @('inspect', '--interactive', '--json') -Window $pickerHwnd
    Invoke-Ui @('set-value', 'FileNameControlHost', 'test.txt') -Window $pickerHwnd
    Invoke-Ui @('invoke', 'Open') -Window $pickerHwnd
    Invoke-Ui @('wait-for', 'StatusBar', '--value', 'opened', '--contains', '-t', '3000')
}

Test-UI 'Copy context menu' {
    Invoke-Ui @('click', 'LstItems', '--right')
    Invoke-Ui @('wait-for', 'MnuCopy', '-t', '2000')
    Invoke-Ui @('invoke', 'MnuCopy')
    Invoke-Ui @('wait-for', 'StatusText', '--value', 'Copied', '-t', '2000')
}

Test-UI 'Confirm deletion' {
    Invoke-Ui @('invoke', 'BtnDelete')
    Invoke-Ui @('wait-for', 'Primary', '-t', '2000')
    Invoke-Ui @('invoke', 'Primary')
    Invoke-Ui @('wait-for', 'Primary', '--gone', '-t', '3000')
}
```

For Save/Cancel pickers, substitute the appropriate action and assertion. MenuBar headers and flyout items use `invoke` and `wait-for` similarly. ContentDialogs live in the app window; `Primary`, `Secondary`, and `Close` are common selectors, but inspect the actual open dialog because custom AutomationIds are not guaranteed.

### Keyboard, hover, drag, touch, and pen

`send-keys --via send-input` is required for accelerators and reliable per-character input in WinUI/WPF text controls. `post-message` is HWND-targeted but does not produce per-character `KeyDown`. `--target` focuses first; `--verbatim` types literal key names. System shortcuts require the CLI's explicit permission flags; do not use them as a workaround for desktop-readiness errors.

```powershell
Test-UI 'Keyboard save' {
    Invoke-Ui @('send-keys', 'hello world', '--target', 'TxtName', '--via', 'send-input')
    Invoke-Ui @('send-keys', 'ctrl+s', '--via', 'send-input')
    Invoke-Ui @('wait-for', 'StatusBar', '--value', 'saved', '--contains', '-t', '2000')
}
Test-UI 'Tooltip' {
    Invoke-Ui @('hover', 'BtnInfo')
    Invoke-Ui @('wait-for', 'InfoTooltip', '-t', '2000')
}
Test-UI 'Reorder items' {
    Invoke-Ui @('drag', 'ItemA', 'ItemB')
    Invoke-Ui @('wait-for', 'StatusBar', '--value', 'reordered', '--contains', '-t', '2000')
}
Test-UI 'Touch and ink input' {
    Invoke-Ui @('touch', 'LstFeed', '-g', 'swipe', '--direction', 'up', '--distance', '400')
    Invoke-Ui @('pen', 'InkCanvas', '--path', '50,50 120,80 200,60', '--pressure', '0.8')
    Save-Screenshot '03-ink.png'
    # Add the app-specific state assertion; successful injection alone is not functional proof.
}
```

Use `winapp ui drag --help`, `winapp ui touch --help`, and `winapp ui pen --help` for coordinates, long-press/dwell, pinch/stretch, pressure/tilt/eraser support. Read coordinates in the guest tree; host coordinates are not guest coordinates.

### Recordings and workflow coordination

```powershell
Test-UI 'Video delivered' {
    $video = Join-Path $ArtifactDirectory 'flow.mp4'
    if (Test-Path -LiteralPath $video) { throw 'Choose a new video evidence path.' }
    Invoke-Ui @('record', '--duration-sec', '6', '--fps', '30', '--output', $video)
    if (-not (Test-Path -LiteralPath $video -PathType Leaf) -or (Get-Item -LiteralPath $video).Length -eq 0) {
        throw "Recording was not delivered to the host: $video"
    }
}
```

Sandbox screenshot/record **`--output` paths are host paths**, automatically delivered when capture completes; do not pull them from guessed guest directories. Use fresh output paths and check the exit status and actual host files. `--capture-screen` can include popups outside the app window. For an interrupted recording, preserve `stopReason`, `partialOutput`, `recoveryHint`, and the reported partial/recovery paths; a nonempty partial file is not necessarily playable.

WinApp CLI arbitrates desktop-changing commands automatically. Without a workflow ID, each command releases its turn immediately; with one, the four-second grace covers tight script bursts, not agent reasoning. Use the **same `WINAPP_UI_WORKFLOW_ID`** for cooperating commands, including concurrent recording and interaction; independent flows need distinct IDs. The template accepts `-WorkflowId` for this purpose, sets it in its own process, restores the previous value, and calls `winapp ui yield --on sandbox` in `finally`. Fresh shell tool calls do not inherit changes from an earlier shell: set the same ID on **every** cooperating invocation. A separate concurrent recording process must also check its exit code/delivery, use the same scoped PID, and finish before the test runner yields. Do not start an independent-ID recording that blocks the interactions it is meant to capture. After a pause, inspect again and reopen menus/dialogs another workflow may have changed.
