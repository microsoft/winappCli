<!-- mslearn: true -->
<!-- description: Use winapp with Python to generate Windows API bindings, run a script with package identity, clean up development identity, and prepare an MSIX. -->

# Using winapp CLI with Python

This guide creates a Python console app that checks its Windows package identity, the app registration required by some Windows APIs. Run it normally to see **Not packaged**, then with `winapp run --identity` to print its package family name.

For complete source, see the [Python identity sample](../../samples/python-identity).

## Prerequisites

- **Windows 11**.
- **Windows CPython 3.11-3.14**, x64 or ARM64.
- **winapp**, installed from PyPI:

```powershell
python -m pip install --upgrade microsoft-winappcli
python -m winappcli --help
```

The package includes the native CLI.

Use this Python package's `winapp` command below. If `winapp` resolves to another installation, use `python -m winappcli` with the Python where you installed the package.

## 1. Prepare the Python project

```powershell
mkdir python-identity
cd .\python-identity
```

## 2. Initialize the project

```powershell
winapp init . --use-defaults --add-python-bindings
```

This prepares SDKs, a manifest and icons, creates `pyproject.toml`, and generates a bindings package under `.winapp\python\winapp_bindings`. If the native step prints `No known project type`, continue when Python setup reports **Python bindings generated**.

To use the Windows SDK's `Package` API, replace `additional-winmds = []` in the `[tool.winapp.python-bindings]` table in `pyproject.toml` with:

```toml
additional-winmds = [
    { namespace = "Windows.ApplicationModel", classes = ["Package"] },
]
```

This adds `Package` without removing the default Windows App SDK bindings. Keep the other settings unchanged, then regenerate:

```powershell
winapp generate
```

## 3. Add the identity check

Create `app.py`:

```python
from dynwinrt import RoApartment, has_package_identity, projected_lifetime_scope
from winapp_bindings.windows.application_model import Package


def main() -> int:
    with RoApartment(1), projected_lifetime_scope():
        if not has_package_identity():
            print("Not packaged")
            return 0
        package = Package.get_current()
        print(f"Package Family Name: {package.id.family_name}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
```

## 4. Run without identity

Install the generated bindings into an ordinary Python environment.

Create `requirements.txt`:

```text
-e '.\.winapp\python\winapp_bindings'
```

Then install and run:

```powershell
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r .\requirements.txt
.\.venv\Scripts\python.exe .\app.py
```

Expected application stdout:

```text
Not packaged
```

Skip environment creation if `.venv` already exists. Keep this environment for packaging in step 7; the identity run below uses its own private Python.

## 5. Run with identity

Enable **Windows Developer Mode** in Windows Settings, then run:

```powershell
winapp run --identity .\app.py
```

Expected output has this shape:

```text
Package Family Name: your-project.debug_publisherhash
```

The runner prepares a private environment matching the Python running winapp, installs the generated bindings, and registers a development package. It does not change your shared Python or ordinary `.venv`. The package name comes from the manifest and publisher.

## 6. Clean up development identity

```powershell
winapp identity clear
```

This unregisters the development package and deletes its private environment, including manually installed packages. It preserves ordinary environments, generated bindings, project files and application data.

If cleanup reports an incomplete update, nothing is cleared. For a project you trust, repeat its original identity command to recover the update, then retry cleanup. Do not run an unfamiliar project solely to clear it.

## 7. Package as MSIX

Build an application directory with PyInstaller, then package that directory with winapp. Use the ordinary `.venv` from [step 4](#4-run-without-identity), not the private identity environment.

### Build the application directory

Create `requirements-dev.txt` next to `requirements.txt` to add the packaging tool:

```text
-r requirements.txt
pyinstaller>=6,<7
```

Install it and build the console application:

```powershell
.\.venv\Scripts\python.exe -m pip install -r .\requirements-dev.txt
.\.venv\Scripts\python.exe -I -B -m PyInstaller `
  --clean --noconfirm --onedir --console --name PythonIdentity `
  --collect-all dynwinrt `
  --collect-all winapp_bindings `
  .\app.py
```

The output is `dist\PythonIdentity\PythonIdentity.exe` plus Python and its dependencies. Keep the entire directory: both `--collect-all` options include dynamically loaded modules and package data. Running the EXE directly still prints `Not packaged`.

### Add a console alias

Register an app execution alias so this console app can be launched as `PythonIdentity.exe` after installation:

```powershell
winapp manifest add-alias --name PythonIdentity.exe
```

### Sign and package

Before packaging for distribution, put the CPython, DynWinRT, PyInstaller and other bundled license notices in `dist\PythonIdentity\licenses`.

Generate a development certificate matching the manifest and package the application directory:

```powershell
winapp cert generate --manifest .\Package.appxmanifest --output .\devcert.pfx
winapp package .\dist\PythonIdentity `
  --manifest .\Package.appxmanifest `
  --executable PythonIdentity.exe `
  --cert .\devcert.pfx `
  --output .\dist\PythonIdentity.msix
```

The result is `dist\PythonIdentity.msix`. Its architecture matches the Python used to build the executable. Keep `devcert.pfx` private; use a release-signing certificate for distribution.

### Install and run

Trust the development certificate from **Administrator PowerShell** in your project directory:

```powershell
winapp cert install .\devcert.pfx
```

Then install and launch the console app from normal PowerShell:

```powershell
Add-AppxPackage .\dist\PythonIdentity.msix
PythonIdentity.exe
```

The installed app prints its package family name instead of `Not packaged`. If Windows cannot find the command, open a new terminal and check the app execution alias in Windows Settings.

Users do not need a separate Python installation. On another machine, install any Windows App Runtime framework dependency declared in the manifest before installing the MSIX.

To install a rebuilt package, increase `Version` in `Package.appxmanifest` first. Uninstall the app through Windows Settings when finished; `winapp identity clear` only removes the development identity. If the certificate was trusted only for this test, remove it from **Certificates (Local Computer) > Trusted People**.

## Next steps

- [Python WinUI sample](../../samples/python-winui): counter launched with ordinary Python.
- [CLI reference](../usage.md): manifests, certificates, signing and packaging.
