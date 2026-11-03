# Python package identity

```powershell
cd .\samples\python-identity
winapp init --use-defaults --add-python-bindings
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r .\requirements.txt
.\.venv\Scripts\python.exe .\app.py
winapp run --identity .\app.py
```

The first run prints **Not packaged**. The identity run prints **Package Family Name:** followed by the registered package's family name. This console sample uses the generated `Windows.ApplicationModel.Package` API, without WinUI or Windows App Runtime Bootstrap code.

For the from-scratch walkthrough, follow the [Python guide](../../docs/guides/python.md). For an unpackaged GUI, use the separate [WinUI sample](../python-winui).

## Prerequisites

Use the guide's [Windows, Python and tool prerequisites](../../docs/guides/python.md#prerequisites). Enable **Windows Developer Mode** before the development-identity run. Run the commands below from this sample directory.

## 1. Initialize and run normally

```powershell
winapp init --use-defaults --add-python-bindings
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r .\requirements.txt
.\.venv\Scripts\python.exe .\app.py
```

Initialization restores SDKs, generates the type selected in `pyproject.toml`, and creates the manifest and assets. It preserves the sample's recorded generator version. The separate pip step installs the generated package and its matching DynWinRT runtime into your application `.venv`; skip environment creation if it already exists.

Expected application stdout:

```text
Not packaged
```

## 2. Run with development identity

```powershell
winapp run --identity .\app.py
```

Expected application stdout has this shape:

```text
Package Family Name: your-project.debug_publisherhash
```

The exact name comes from the project manifest and publisher. The runner prepares a private Python runtime, installs this sample's generated bindings and their DynWinRT runtime, and registers a development package. See [Choosing Python](../../docs/guides/python.md#choosing-python) for the default interpreter and [identity dependencies](../../docs/guides/python.md#identity-dependencies) for optional requirements, updates and manual installs. First preparation downloads the runtime if it is not cached; later runs reuse it. Ordinary `.venv` execution is unchanged.

Unexpected API failures are reported as errors, not converted to **Not packaged**.

## 3. Clear development identity

```powershell
winapp identity clear
```

This unregisters the development package and removes the private environment. It preserves the ordinary `.venv`, bindings, project files and application data.

## 4. Package as MSIX

Build a console executable with PyInstaller, then package its complete output directory. The private development runtime is not a distribution artifact.

### Restore build dependencies

```powershell
winapp restore
.\.venv\Scripts\python.exe -m pip install -r .\requirements-dev.txt
```

`requirements-dev.txt` includes the application requirements and PyInstaller. winapp restores bindings first; pip installs build dependencies afterward.

### Build the application directory

```powershell
.\.venv\Scripts\python.exe -I -B -m PyInstaller `
  --clean --noconfirm --onedir --console --name PythonIdentity `
  --collect-all dynwinrt `
  --collect-all winapp_bindings `
  .\app.py
```

The output is `dist\PythonIdentity\PythonIdentity.exe` plus its Python runtime and dependencies. Running that EXE directly still prints **Not packaged**. Package the whole directory, not only the EXE. Its architecture matches the build interpreter.

Collect both complete packages so dynamically loaded Python modules, Windows App SDK configuration metadata and Bootstrap DLLs are included. `--collect-submodules` alone does not collect those resources. The same collection options apply to `--onefile`; the MSIX example uses `--onedir`.

### Add a console alias and sign the package

Add the installed command name to the manifest:

```powershell
winapp manifest add-alias --name PythonIdentity.exe
winapp cert generate --manifest .\Package.appxmanifest --output .\devcert.pfx
winapp package .\dist\PythonIdentity `
  --manifest .\Package.appxmanifest `
  --executable PythonIdentity.exe `
  --cert .\devcert.pfx `
  --output .\dist\PythonIdentity.msix
```

Keep `devcert.pfx` private. Use a release-signing certificate for distribution. Before redistributing, include CPython, DynWinRT, PyInstaller and other bundled dependency license notices under `dist\PythonIdentity\licenses`.

The MSIX includes Python and the application dependencies; it does not copy `.venv` or `.winapp\python-identity`. The target machine must also have any Windows App Runtime dependency declared in the manifest. Project initialization installs it on the build machine.

### Install and run

Trust the development certificate from **Administrator PowerShell** in this sample directory:

```powershell
winapp cert install .\devcert.pfx
```

In normal PowerShell:

```powershell
Add-AppxPackage .\dist\PythonIdentity.msix
PythonIdentity.exe
```

The installed alias prints its package family name. If Windows cannot find the command, open a new terminal and check the app execution alias in Windows Settings. Running the EXE directly from `dist` is not the same as launching the installed package.

Uninstall through Windows Settings when finished. `identity clear` only removes the development registration, not the installed MSIX. If the certificate was trusted only for this test, remove it from **Certificates (Local Computer) > Trusted People** when no longer needed.

## Restore and troubleshoot

After cloning, run `winapp restore`, create `.venv`, and install `requirements.txt` as above. For API selections, version updates and errors, see the [Python guide](../../docs/guides/python.md#troubleshooting).

If MSIX installation reports `0x800B0109`, trust the matching certificate as described above. A missing framework dependency requires installing the manifest's requested Windows App Runtime version and architecture on the target machine.

## Test

From the repository root, with Pester 5 and a built Python wheel:

```powershell
.\scripts\test-samples.ps1 -Samples python-identity -WinappPath .\artifacts\python
```
