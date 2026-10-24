# Python WinUI counter

```powershell
cd .\samples\python-winui
winapp init --use-defaults --add-python-bindings
winapp run .\app.py
```

This sample runs a code-only WinUI 3 counter in ordinary Python without package identity, a C# host or XAML. Windows App Runtime is initialized with its Bootstrap DLL. For identity and MSIX packaging, use the separate [Python identity sample](../python-identity).

## Prerequisites

Follow the [Python guide's installation prerequisites](../../docs/guides/python.md#prerequisites). Run commands from this sample directory in normal PowerShell; no environment activation or Developer Mode is needed.

WinUI hosting through DynWinRT is experimental.

## 1. Initialize the sample

If needed, clone this repository from a separate working directory:

```powershell
git clone https://github.com/microsoft/winappCli.git
cd .\winappCli\samples\python-winui
```

Prepare SDKs, the Windows App Runtime, generated bindings and the ordinary project `.venv`:

```powershell
winapp init --use-defaults --add-python-bindings
```

Continue when it reports **Python bindings ready**. The sample's type selections are already in `pyproject.toml`; initialization preserves them.

## 2. Run the counter

```powershell
winapp run .\app.py
```

The window shows **Bootstrap - memory only** and starts at **0**. Click **+** to increment and **-** to decrement. The minus button is disabled at zero. The count has a fixed-width area, so changing the number of digits does not move the buttons.

Close the window and run again: the count returns to **0**. No counter data is saved. The terminal prints the Bootstrap DLL and Python interpreter used by the app.

## Restore and customize

After cloning an initialized project, restore its recorded versions:

```powershell
winapp restore
```

Edit the existing `types` list in `pyproject.toml`, then run `winapp generate` to change the API surface. Use `winapp update` to advance SDK and generator versions. See [bindings configuration](../../docs/guides/python.md#configure-bindings).

| File | Purpose |
|---|---|
| `app.py`, `counter.py`, `window.py` | In-memory counter and WinUI events |
| `runtime.py` | Bootstrap initialization and WinRT lifetime |
| `pyproject.toml` | Selected types and generator version |

## Troubleshooting

| Problem | Action |
|---|---|
| Bootstrap DLL or SDK metadata is missing | Run `winapp restore`. |
| WinUI or an import fails | Read the terminal error, verify Windows/Python prerequisites, then restore and regenerate. |
| You need identity or an installable MSIX | Follow the [Python identity sample](../python-identity); this sample only demonstrates unpackaged GUI execution. |

## Test

```powershell
python -m unittest -v test_app
```

From the repository root, with Pester 5 and a built Python platform wheel:

```powershell
.\scripts\test-samples.ps1 -Samples python-winui -WinappPath .\artifacts\python
```
