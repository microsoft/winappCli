<!-- mslearn: true -->
<!-- description: Use winapp with Python to generate Windows API bindings, run a script with package identity, clean up development identity, and prepare an MSIX. -->

# Using winapp CLI with Python

```powershell
winapp-py run .\app.py
winapp-py run --identity .\app.py
```

This guide builds a small console app that prints **Not packaged** during ordinary execution and its package family name when run with Windows package identity. Identity enables Windows APIs that require a registered app. No WinUI window or C# host is needed.

For the complete source and packaging walkthrough, use the [Python identity sample](../../samples/python-identity). For code-only WinUI windows and a file picker without identity, use the separate [WinUI sample](../../samples/python-winui).

## Prerequisites

- **Windows 11**.
- **Windows CPython 3.11-3.14**, x64 or ARM64.
- **winapp-py**, installed from PyPI:

```powershell
python -m pip install --upgrade winapp-cli
winapp-py --help
```

The package includes the native CLI; no separate winapp or Node.js installation is needed.

## 1. Prepare the Python project

```powershell
mkdir python-identity
cd .\python-identity
```

Create `pyproject.toml` with your application's metadata:

```toml
[project]
name = "python-identity"
version = "0.0.0"
requires-python = ">=3.11,<3.15"
dependencies = []
```

## 2. Initialize the project

```powershell
winapp-py init . --use-defaults --add-python-bindings
```

This creates or reuses `.venv`, prepares SDKs and projections, creates the manifest and icons, and adds a bindings table to `pyproject.toml`. Continue when it reports **Python bindings ready**.

The guide uses the Windows SDK's `Package` API. In the table that initialization created, change only these two settings; keep its recorded `codegen-version` and other settings:

```toml
scope = "types"
types = ["Windows.ApplicationModel.Package"]
```

Generate and install those bindings:

```powershell
winapp-py generate
```

No manual `pip install` of DynWinRT or activation of `.venv` is needed.

### Selecting an initialization directory

An explicit path targets that directory. Without a path, `--use-defaults` targets the current directory:

```powershell
winapp-py init .\python-identity --use-defaults --add-python-bindings
```

Interactive init without a directory asks you to confirm the bindings target from directories with usable SDK configuration after native setup. These include previously prepared projects. Select and confirm the intended target, or decline to leave Python files unchanged. If no usable target is found, rerun with its explicit directory.

`--config-only` prepares configuration and defers generation until `restore`. Normal generation requires matching project-local `winapp.yaml` and `.winapp\winmds.lock.json`. `--sparse`, `--setup-sdks none` and `--ignore-config` do not enable Python bindings. See the [`init` reference](../usage.md#init) for native project hints and subdirectory searching.

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

The script checks identity before calling the projected `Package` API. Unexpected API failures remain errors rather than being reported as **Not packaged**.

## 4. Run without identity

```powershell
winapp-py run .\app.py
```

Expected application stdout:

```text
Not packaged
```

The command uses the project root's existing `.venv`, without installing dependencies or assigning identity. A missing interpreter produces manual-run guidance instead of falling back to global Python.

## 5. Run with identity

Enable **Windows Developer Mode** in Windows Settings, then run:

```powershell
winapp-py run --identity .\app.py
```

Expected output has this shape:

```text
Package Family Name: your-project.debug_publisherhash
```

The name depends on the manifest and publisher. The runner prepares a private Python runtime matching the project's interpreter, installs declared application dependencies, and registers a development package. The first preparation can download a runtime and take longer than later runs; the ordinary `.venv` is unchanged.

This is a development workflow, not a distributable Python application.

## 6. Clean up development identity

```powershell
winapp-py identity clear
```

This unregisters the development package and removes its private environment, while preserving `.venv`, bindings, project files and application data.

## 7. Package as MSIX

Use PyInstaller or another Python packaging tool to build an application directory, then pass that directory to `winapp-py package`. Follow the [identity sample's MSIX walkthrough](../../samples/python-identity/README.md#4-package-as-msix) for a complete console build, signing, installation and execution-alias example.

The private development runtime is not the distribution payload. Users of the packaged application do not need a separate Python installation.

## Restore and update

After cloning an initialized project or removing generated files:

```powershell
winapp-py restore
```

To change API selections, edit the existing `types` list and run `winapp-py generate`. To update SDK and generator versions, run `winapp-py update`.

## Tips

### Configure bindings

Use `scope = "types"` for explicitly selected Windows SDK or WinUI APIs. Use `scope = "default"` to retain the default Windows App SDK bindings and add other APIs through `types`. Keep one bindings table in `pyproject.toml`, created by `init`; edit it rather than adding a duplicate.

### Dependency declarations

Initial setup records the resolved `codegen-version` in `[tool.winapp.python-bindings]` in your project's `pyproject.toml`; commit it with the project. `restore` and `generate` reuse or install that exact generator and install the matching `dynwinrt` runtime through the generated package. Neither package needs a direct application dependency declaration. `update` advances the recorded versions.

Declare application dependencies in project metadata or requirements files. The private identity environment does not copy manually installed packages from `.venv`.

### Output options

```powershell
winapp-py --quiet restore
winapp-py run .\app.py -- --json
```

Use `--quiet` (`-q`) to hide wrapper status or `--verbose` (`-v`) to show tool diagnostics. Put wrapper options before the Python script. Arguments after the script, with or without `--`, pass unchanged to the application. Quiet mode does not suppress application output or errors.

Python workflows do not support `--json`; native commands retain their supported JSON output. Interpreter selection and identity-preparation status go to stderr, leaving application stdout available for piping.

### Editor workflow

Select `.venv\Scripts\python.exe` as the editor interpreter. Do not edit generated files under `.winapp`. Use `winapp-py <command> --help` for options and `winapp-py --version` for package and native CLI versions.

## Troubleshooting

| Problem | Action |
|---|---|
| `Package` cannot be imported | Select `Windows.ApplicationModel.Package` in the existing bindings table, then run `winapp-py generate`. |
| SDK metadata is missing or out of date | Run `winapp-py restore`. |
| The script still prints `Not packaged` | Use `run --identity` and check the terminal's preparation errors. |
| Identity preparation fails | Read the reported cause, verify Developer Mode and the project interpreter, then retry. |
| Ordinary `run` cannot find `.venv` | Use your chosen interpreter or `uv run`; the wrapper does not create an environment during ordinary execution. |
| DynWinRT dependency versions conflict | Remove direct generator/runtime declarations from application dependencies, then run `winapp-py generate`. |
| No codegen version is recorded | Run `winapp-py init . --use-defaults --add-python-bindings` to record a version without resetting API selections, or `winapp-py update` to choose a new version. |

## Next steps

- [Python identity sample](../../samples/python-identity): complete console source and manual MSIX packaging.
- [Python WinUI sample](../../samples/python-winui): Bootstrap-based counter and file picker, without development identity.
- [CLI reference](../usage.md): manifests, certificates, signing and packaging.
