# Python WinUI counter

This is a code-only WinUI 3 counter without a C# host or XAML. WinUI hosting through DynWinRT is experimental.

Follow the [Python prerequisites](../../docs/guides/python.md#prerequisites). The counter runs without package identity; Developer Mode is not required to launch it.

## 1. Initialize the sample

Prepare stable SDKs, the Windows App Runtime and generated bindings:

```powershell
cd .\samples\python-winui
winapp init --use-defaults --add-python-bindings
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r .\requirements.txt
```

Skip environment creation if `.venv` already exists.

## 2. Run the counter

```powershell
.\.venv\Scripts\python.exe .\app.py
```

The window shows **Bootstrap - memory only** and starts at **0**. Click **+** to increment and **-** to decrement; minus is disabled at zero. Closing and reopening the counter resets it to **0**.

The sample's [`runtime.py`](runtime.py) initializes the WinUI thread and calls `dynwinrt.auto_init` to load the installed Windows App Runtime.

## Package as MSIX

Follow the [Python guide's MSIX packaging steps](../../docs/guides/python.md#7-package-as-msix), using `--windowed` instead of `--console`. Skip the console alias step and launch the installed app from the Start menu.

## Test

```powershell
python -m unittest -v test_app
```

From the repository root, with Pester 5 and a built Python platform wheel:

```powershell
.\scripts\test-samples.ps1 -Samples python-winui -WinappPath .\artifacts\python
```
