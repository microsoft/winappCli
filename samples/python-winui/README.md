# Python WinUI counter

```powershell
cd .\samples\python-winui
winapp init --use-defaults --add-python-bindings
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r .\requirements.txt
.\.venv\Scripts\python.exe .\app.py
```

This sample runs a code-only WinUI 3 counter without a C# host or XAML. Ordinary Python uses the Bootstrap DLL bundled in its generated bindings. The same application can run with development identity, which skips Bootstrap. For MSIX packaging, use the [Python identity sample](../python-identity).

## Prerequisites

Follow the [Python guide's installation prerequisites](../../docs/guides/python.md#prerequisites). Run commands from this sample directory in normal PowerShell; no environment activation or Developer Mode is needed.

WinUI hosting through DynWinRT is experimental.

## 1. Initialize the sample

If needed, clone this repository from a separate working directory:

```powershell
git clone https://github.com/microsoft/winappCli.git
cd .\winappCli\samples\python-winui
```

Prepare SDKs, the Windows App Runtime and generated bindings:

```powershell
winapp init --use-defaults --add-python-bindings
```

Continue when it reports **Python bindings generated**. The sample's type selections are already in `pyproject.toml`; initialization preserves them. Then create the application environment and install the generated package with pip:

```powershell
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r .\requirements.txt
```

Skip environment creation if `.venv` already exists. The generated package brings its matching DynWinRT runtime; the generator is not installed into the application's environment.

The sample uses stable Windows App SDK packages. Its `runtime.py` imports `dynwinrt.auto_init` inside the WinUI thread's lifetime scope. See [Windows App SDK startup](../../docs/guides/python.md#windows-app-sdk-startup) for how installed bindings supply the configuration and Bootstrap DLLs.

## 2. Run the counter

```powershell
.\.venv\Scripts\python.exe .\app.py
```

The window shows **Bootstrap - memory only** and starts at **0**. Click **+** to increment and **-** to decrement. The minus button is disabled at zero. The count has a fixed-width area, so changing the number of digits does not move the buttons.

Close the window and run again: the count returns to **0**. No counter data is saved. The terminal prints the execution mode and Python interpreter used by the app.

### Run the same counter with identity

Enable Windows Developer Mode, then run:

```powershell
winapp run --identity .\app.py
```

The window shows **Package identity - memory only**. Windows supplies the manifest's Windows App Runtime dependency; auto-init does not load Bootstrap. This uses a private Python environment without changing `.venv`; see [Choosing Python](../../docs/guides/python.md#choosing-python) to select the interpreter it should match. Close the window, then remove the development registration:

```powershell
winapp identity clear
```

## Restore and customize

After cloning an initialized project, restore its recorded versions:

```powershell
winapp restore
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r .\requirements.txt
```

Edit the existing `types` list in `pyproject.toml`, then run `winapp generate` to change the API surface. Use `winapp update` to advance SDK and generator versions, then rerun the pip install to synchronize runtime dependencies. See [bindings configuration](../../docs/guides/python.md#configure-bindings).

| File | Purpose |
|---|---|
| `app.py`, `counter.py`, `window.py` | In-memory counter and WinUI events |
| `runtime.py` | STA and projection lifetime, with opt-in Bootstrap auto-initialization |
| `pyproject.toml` | Selected types and generator version |
| `requirements.txt` | Editable generated bindings for the application environment |

## Troubleshooting

| Problem | Action |
|---|---|
| Bootstrap DLL or SDK metadata is missing | Run `winapp restore`. |
| Auto-init cannot find Windows App SDK configuration | Run `winapp restore` with stable SDK packages, then install `requirements.txt` into the application interpreter. |
| WinUI or an import fails | Read the terminal error, verify Windows/Python prerequisites, restore bindings, and install `requirements.txt` into the application environment. |
| You need an installable MSIX | Follow the [Python identity sample](../python-identity). |

## Test

```powershell
python -m unittest -v test_app
```

From the repository root, with Pester 5 and a built Python platform wheel:

```powershell
.\scripts\test-samples.ps1 -Samples python-winui -WinappPath .\artifacts\python
```

The development-identity case runs when Developer Mode is already enabled; the tests do not enable it.
