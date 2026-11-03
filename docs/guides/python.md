<!-- mslearn: true -->
<!-- description: Use winapp with Python to generate Windows API bindings, run a script with package identity, clean up development identity, and prepare an MSIX. -->

# Using winapp CLI with Python

```powershell
.\.venv\Scripts\python.exe .\app.py
winapp run --identity .\app.py
```

This guide builds a small console app that prints **Not packaged** during ordinary execution and its package family name when run with Windows package identity. Identity enables Windows APIs that require a registered app. No WinUI window or C# host is needed.

For the complete source and packaging walkthrough, use the [Python identity sample](../../samples/python-identity). For a code-only WinUI counter with ordinary or identity-backed execution, use the [WinUI sample](../../samples/python-winui).

## Prerequisites

- **Windows 11**.
- **Windows CPython 3.11-3.14**, x64 or ARM64.
- **winapp**, installed from PyPI:

```powershell
python -m pip install --upgrade microsoft-winappcli
python -m winappcli --help
```

The package includes the native CLI; no separate winapp or Node.js installation is needed. An isolated installation with `pipx install microsoft-winappcli` or `uv tool install microsoft-winappcli` keeps the tool available before application dependencies are restored.

The `winapp` commands below refer to this Python package's entry point. If winget or npm also provides a `winapp` command, use `python -m winappcli` in its place, with the Python environment where you installed `microsoft-winappcli`.

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
winapp init . --use-defaults --add-python-bindings
```

This prepares SDKs, the manifest and icons, adds a bindings table to `pyproject.toml`, and generates an installable package under `.winapp\python\winapp_bindings`. It does not create or change your application environment, dependency declarations or lockfiles. A `No known project type` warning during the native step does not prevent Python setup; continue when it reports **Python bindings generated**.

The guide uses the Windows SDK's `Package` API. In the table that initialization created, change only these two settings; keep its recorded `codegen-version` and other settings:

```toml
scope = "types"
types = ["Windows.ApplicationModel.Package"]
```

Generate those bindings:

```powershell
winapp generate
```

### Install bindings into the application

For this pip-based walkthrough, create `requirements.txt`:

```text
-e '.\.winapp\python\winapp_bindings'
```

Create the application environment and install its dependencies:

```powershell
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r .\requirements.txt
```

The generated package declares the matching `dynwinrt` runtime, so you do not install it separately. Add your other application dependencies to the same requirements file. For existing Poetry or uv projects, use your own manager instead; see [dependency declarations](#dependency-declarations).

With DynWinRT `0.1.0rc24` or later, the generated project uses a `src` layout:

```text
.winapp\python\winapp_bindings\
  pyproject.toml
  src\winapp_bindings\
    __init__.py
    py.typed
```

Install the outer directory containing `pyproject.toml`, not its `src` directory. Imports still start with `winapp_bindings`, not `src`.

### Selecting an initialization directory

Use an explicit path for a Python project, or `--use-defaults` for the current directory. Native project search does not discover Python projects:

```powershell
winapp init .\python-identity --use-defaults --add-python-bindings
```

Interactive init without a directory asks you to confirm the bindings target from directories with usable SDK configuration after native setup. These include previously prepared projects. Select and confirm the intended target, or decline to leave Python files unchanged. If no usable target is found and bindings were not explicitly requested, native initialization remains successful and Python setup is skipped. To add bindings, rerun with the Python project's explicit directory and `--add-python-bindings`.

Use a separate directory from C# projects for Python bindings. In a directory containing a `.csproj` file, `winapp init . --use-defaults --add-python-bindings` completes native initialization, then reports that Python bindings are unsupported and exits with code `1`. Native initialization changes are kept; Python configuration and environments are unchanged. Without requested or already-configured Python bindings, C# initialization succeeds normally.

`--config-only` prepares configuration and defers generation until `restore`. Normal generation requires matching project-local `winapp.yaml` and `.winapp\winmds.lock.json`. `--sparse`, `--setup-sdks none` and `--ignore-config` do not enable Python bindings. See the [`init` reference](../usage.md#init) for other initialization options.

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
.\.venv\Scripts\python.exe .\app.py
```

Expected application stdout:

```text
Not packaged
```

Run ordinary scripts with your application interpreter, package manager or IDE. This guide uses `.venv`; a Poetry project can use `poetry run python .\app.py` instead. `winapp run` accepts Python scripts only with `--identity`; native EXE and .NET run modes are unchanged.

## 5. Run with identity

Enable **Windows Developer Mode** in Windows Settings, then run:

```powershell
winapp run --identity .\app.py
```

Expected output has this shape:

```text
Package Family Name: your-project.debug_publisherhash
```

The name depends on the manifest and publisher. The runner prepares a private Python runtime, installs generated bindings and their DynWinRT runtime there, and registers a development package. The selected interpreter and its environment are unchanged. You do not need a separate ordinary `.venv` or a requirements file just to use identity.

### Choosing Python

By default, the private runtime matches the Python running winapp (`sys.executable`). This may be a global Python, a project venv, or an isolated uv tool/pipx environment. winapp does not search the project's `.venv`, PATH or package-manager configuration for another interpreter. Activating a venv does not change this if `winapp` still resolves to a global or isolated tool installation.

To match a different interpreter, pass its executable path:

```powershell
winapp run --identity --python .\.venv\Scripts\python.exe .\app.py
```

`--python` selects the version, architecture and build variant to match; it does not add identity to that executable or copy its installed dependencies.

### Identity dependencies

Generated bindings and their matching DynWinRT runtime are installed automatically. To also install application dependencies, explicitly provide a pip requirements file:

```powershell
winapp run --identity --requirements .\requirements.txt .\app.py
```

The file is optional. Without `--requirements`, winapp does not install application dependencies from an existing `requirements.txt`, `pyproject.toml` or lockfile, and does not invoke uv, Poetry or another application manager. Project dependency files are left unchanged.

Use pip requirements syntax, not a lockfile from another manager. You can export requirements with your manager or maintain the file yourself. Include the application itself if it needs installation, for example `-e .`. pip installs the listed packages and their dependencies; the file need not list every indirect dependency. Hash-enforcing requirements cannot be combined with local editable bindings; export without hashes rather than changing the original lockfile. Installation failures stop the run before the application starts.

To install an additional package manually, use the actual **Identity Python** path printed by winapp. For example, replace the placeholder below with that path:

```powershell
python -m pip --python '<Identity Python path printed by winapp>' install requests
```

Use a Python with pip available; winapp's dependency hint includes a full command using its own Python. A failed application keeps its original error and exit code; the installation hint does not guess a distribution name from an import such as `PIL`. Installing a package into an unrelated `.venv` does not install it into the identity environment.

Ordinary runs reuse the private environment. Changes to explicitly supplied requirements or generated binding dependencies update it in place without removing unrelated manually installed packages. Omit `--requirements` on a later run to keep using installed packages without synchronizing that file. If pip fails, some packages may already have changed; fix its reported error and retry before running the application. Manifest or SDK changes update the registration without recreating Python. Editable source changes are visible immediately; reinstall a manually installed non-editable application when its source changes.

Changing the selected Python version, architecture or build variant prepares a replacement runtime. winapp records the old environment's installed packages and restores the additional packages alongside the current generated bindings. The old environment is retained until installation and identity verification succeed. Unavailable sources, incompatible versions or restoration failures stop the run; failed registration updates restore the previous registration, and interrupted updates are recovered on the next run. Snapshots are internal recovery information, not portable lockfiles.

This is a development workflow, not a distributable Python application.

### Runtime catalog pinning

`run --identity` uses a Python Build Standalone (PBS) download catalog fixed by the installed `microsoft-winappcli` release. It matches the chosen interpreter's exact Python version, architecture and build variant (regular, free-threaded or debug), without changing `pyproject.toml`. Runtime preparation uses PBS directly and does not require uv.

The first preparation needs network access to download the catalog and runtime. Both downloads are checked against their SHA-256 hashes before use; no GitHub Release API lookup is needed. If the catalog has no exact match, the command stops rather than choosing another Python version. Upgrade `microsoft-winappcli` for a newer catalog, or pass `--python` with an interpreter covered by the installed release.

An unchanged prepared identity reuses its runtime without downloading the catalog, including after a tool upgrade. To select a runtime from the upgraded tool's catalog, run `winapp identity clear`, then repeat the identity command.

## 6. Clean up development identity

```powershell
winapp identity clear
```

This unregisters the development package and removes its private environment, including manually installed packages and recovery snapshots. It preserves ordinary environments, generated bindings, project files and application data.

If an identity update was interrupted, cleanup stops without changing its recovery files. It does not restore registrations or start the private Python. For a project you trust, repeat its original `winapp run --identity ...` command to recover the update, then retry cleanup. Do not run an unfamiliar project solely to clear it.

## 7. Package as MSIX

Use PyInstaller or another Python packaging tool to build an application directory, then pass that directory to `winapp package`. Follow the [identity sample's MSIX walkthrough](../../samples/python-identity/README.md#4-package-as-msix) for a complete console build, signing, installation and execution-alias example.

The private development runtime is not the distribution payload. Users of the packaged application do not need a separate Python installation.

## Restore and update

After cloning an initialized project or removing generated files:

```powershell
winapp restore
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r .\requirements.txt
```

Skip environment creation if `.venv` already exists. Restore bindings **before** installing application dependencies, because the local package must exist first. For Poetry, the equivalent is `winapp restore` followed by `poetry install`.

To change API selections, edit the existing `types` list and run `winapp generate`. Editable installations see regenerated code without reinstalling when dependency metadata is unchanged; non-editable installations need reinstalling.

| Command | Version behavior |
|---|---|
| `init` | Select and record a generator version on first setup; preserve existing pins. |
| `restore` | Restore pinned SDKs and the recorded generator, then generate bindings. |
| `generate` | Use existing SDK metadata and the recorded generator; do not upgrade. |
| `update` | Update SDK and generator pins, restore SDKs, and regenerate bindings. |

Each Python bindings workflow prepares its generator in a new temporary `.winapp\.codegen-*` directory, including `init --config-only`. The environment is removed when the workflow exits; generated bindings remain in `.winapp\python`. Existing generator directories are not reused. The recorded version stays fixed except during `update` or first-time setup. Preparing the generator requires package-source access unless pip can satisfy the installation from its own caches, even when unchanged bindings let winapp skip generation.

After `winapp update`, synchronize application dependencies again: rerun the pip install above, `poetry update winapp-bindings`, or your manager's equivalent. None of these winapp commands installs application dependencies.

## Tips

### Configure bindings

Use `scope = "types"` for explicitly selected Windows SDK or WinUI APIs. Use `scope = "default"` to retain the default Windows App SDK bindings and add other APIs through `types`. Keep one bindings table in `pyproject.toml`, created by `init`; edit it rather than adding a duplicate.

### Windows App SDK startup

After installing the generated bindings, initialize a WinUI application before creating Windows App SDK objects:

```python
from dynwinrt import RoApartment, projected_lifetime_scope

with RoApartment(0), projected_lifetime_scope():
    import dynwinrt.auto_init

    # Run the WinUI application inside this scope.
```

`RoApartment(0)` initializes the calling thread as an STA, the COM threading mode WinUI requires. The inner lifetime scope releases projected objects before that thread is uninitialized.

For stable Windows App SDK packages, winapp supplies the restored SDK version and x64/ARM64 Bootstrap DLLs to DynWinRT's generator. The installed bindings carry that configuration and the DLLs, so startup does not depend on the project's `.winapp\bin` path. `dynwinrt.auto_init` uses the installed configuration for ordinary processes and skips Bootstrap when the process already has package identity. Ordinary runtime and generated-binding imports do not initialize Bootstrap.

Bootstrap makes an installed Windows App Runtime available to the process; bundling its DLL does not deploy the full runtime. Identity-backed applications instead need the framework dependencies declared in their manifest. Preview Windows App SDK versions do not get automatic Bootstrap configuration. Use stable SDK packages for this workflow.

For the complete startup and identity commands, see the [WinUI sample](../../samples/python-winui). When packaging with PyInstaller, collect both complete packages as shown in the [packaging walkthrough](../../samples/python-identity/README.md#build-the-application-directory).

### Dependency declarations

winapp owns the generator and generated package; your package manager owns the application environment and dependency files. The generator is not an application dependency. The generated package requires its matching `dynwinrt`; version conflicts are reported by the package manager.

The pip example above records the binding package in `requirements.txt`. With uv, run `uv add --editable .\.winapp\python\winapp_bindings` after generation.

For Poetry, keep the local source **relative** so a clone works in a different directory. Add these entries to the existing tables without replacing other dependencies:

```toml
[project]
requires-python = ">=3.11,<3.15"
dependencies = ["winapp-bindings"]

[tool.poetry]
package-mode = false

[tool.poetry.dependencies]
winapp-bindings = { path = '.winapp\python\winapp_bindings', develop = true }
```

`package-mode = false` is for this script-only example; omit it for an installable application package. Then run `poetry install` and `poetry run python .\app.py`. Some Poetry versions record an absolute file URI in `[project].dependencies` when using `poetry add --editable`; use the relative source declaration above instead. The application's declared Python range must also fit the generated package's range.

PDM and Pipenv users likewise add the generated directory as a local package with their manager. Commit `pyproject.toml`, `winapp.yaml`, application dependency declarations and lockfiles. Do not commit `.winapp`, which `winapp restore` recreates.

### Output options

```powershell
winapp --quiet restore
winapp run --identity .\app.py -- --json
```

Use `--quiet` (`-q`) to hide wrapper status or `--verbose` (`-v`) to show tool diagnostics. For identity runs, put wrapper options before the Python script. Arguments after the script, with or without `--`, pass unchanged to the application. Quiet mode does not suppress application output or errors.

Python workflows do not support `--json`; native commands retain their supported JSON output. Identity-preparation status goes to stderr, leaving application stdout available for piping.

### Editor workflow

Select `.venv\Scripts\python.exe` as the editor interpreter. Do not edit generated files under `.winapp`. Use `winapp <command> --help` for options and `winapp --version` for package and native CLI versions.

## Troubleshooting

| Problem | Action |
|---|---|
| `Package` cannot be imported | Select `Windows.ApplicationModel.Package`, run `winapp generate`, then install the generated package with your application manager. |
| SDK metadata is missing or out of date | Run `winapp restore`. |
| The script still prints `Not packaged` | Use `run --identity` and check the terminal's preparation errors. |
| Identity preparation fails | Read the reported cause, verify Developer Mode and the [selected Python](#choosing-python), then retry. |
| `winapp run app.py` requires `--identity` | For ordinary execution, use your application interpreter or package manager; see [run without identity](#4-run-without-identity). |
| DynWinRT dependency versions conflict | Reconcile application constraints with the generated package's required runtime, then reinstall with your package manager. Do not force an incompatible runtime. |
| The bindings package was renamed | Update the application dependency name and local path with your manager. winapp does not rewrite them. |
| Generator preparation fails | Check the reported Python, package-source or filesystem error, then retry; the next attempt creates a new temporary environment. |
| Codegen is missing `python.winappsdk-bootstrap-config` | The recorded generator predates this workflow. Run `winapp update`, then synchronize application dependencies. |
| Auto-init reports no Windows App SDK configuration | Restore bindings with a stable Windows App SDK, then install the outer generated project into the interpreter running the app. Adding `src` to `PYTHONPATH` alone does not install the configuration metadata. |
| Identity reports a missing application package | See [identity dependencies](#identity-dependencies): pass `--requirements` or install into the printed Identity Python, not an unrelated environment. |
| Identity reports hash enforcement | Supply an explicit pip requirements input compatible with editable bindings; the original file and its hash policy remain unchanged. |
| Runtime replacement cannot restore a package | Check the reported source, version or compatibility error. The old environment is retained; do not clear it if you still need its packages. |
| No codegen version is recorded | Run `winapp init . --use-defaults --add-python-bindings` to record a version without resetting API selections, or `winapp update` to choose a new version. |
| No unique PBS distribution matches the selected Python | See [runtime catalog pinning](#runtime-catalog-pinning) for tool upgrades and interpreter selection. |

## Next steps

- [Python identity sample](../../samples/python-identity): complete console source and manual MSIX packaging.
- [Python WinUI sample](../../samples/python-winui): counter with Bootstrap or development identity.
- [CLI reference](../usage.md): manifests, certificates, signing and packaging.
