# Developing winapp for Python

This page covers source setup, building platform wheels and validating the Python wrapper. For application usage, follow the [Python guide](../../../docs/guides/python.md).

## Developing this package

Source development requires Windows, CPython 3.11-3.14 and the .NET 10 SDK. Run these commands from the repository root:

```powershell
python -m venv .\src\winapp-python\.venv
.\src\winapp-python\.venv\Scripts\Activate.ps1
python -m pip install -e .\src\winapp-python

dotnet build .\src\winapp-CLI\WinApp.Cli\WinApp.Cli.csproj -c Debug -r win-x64 `
  -p:PlatformTarget=x64 -p:PublishAot=false `
  -p:PublishSingleFile=false -p:SelfContained=false

$env:WINAPP_CLI_PATH = (Resolve-Path `
  .\src\winapp-CLI\WinApp.Cli\bin\Debug\net10.0-windows10.0.19041.0\win-x64\winapp.exe).Path

winapp-py --help
```

For ARM64 Python, use `win-arm64` and `-p:PlatformTarget=ARM64`. Editable installations have no bundled native CLI. Lookup order is an explicit `--winapp <path>`, `WINAPP_CLI_PATH`, the installed bundle, then `winapp.exe` on `PATH` for a source installation. A damaged platform-wheel bundle fails rather than silently selecting another binary.

## Build and install a local wheel

To build a local `winapp-cli` wheel, run one command from the repository root:

```powershell
.\scripts\package-python.ps1 -BuildCli
```

Windows CPython 3.11-3.14, PowerShell 7, the .NET 10 SDK and Visual Studio C++ build tools for the target architecture are required. If publishing reports a missing platform linker, install the C++ tools for that architecture before retrying.

The script selects the architecture of `python` by default, publishes a fresh Release native CLI, then packages it with the Python wrapper. It includes the license, omits PDBs and checks the executable architecture. Native publishing uses a temporary directory that is removed afterward; existing native publishes and unrelated artifacts are not deleted.

The wheel is written to `artifacts\python`. Its version uses the same repository `version.json`, build number and prerelease label as native and npm packages. For example, shared version `0.7.2-prerelease.37` produces Python version `0.7.2.dev37` and an x64 wheel named `winapp_cli-0.7.2.dev37-py3-none-win_amd64.whl`; ARM64 uses the corresponding `win_arm64` tag. Source `pyproject.toml` keeps its editable-development version; only the staged copy receives the build version. The script does not upload to PyPI, install or replace your tool, or package the counter application.

Use `-Stable` for the base release version, or `-Version <shared-build-version>` to consume a version already selected by the build. Existing native publishes must match that shared version; rebuild them or provide their correct `-Version` if packaging reports a mismatch.

### Select another architecture or interpreter

Use `-Architecture x64`, `-Architecture arm64` or `-Architecture All` to select targets explicitly. Building both architectures requires both sets of C++ tools:

```powershell
.\scripts\package-python.ps1 -BuildCli -Architecture All
```

Pass `-Python <python.exe>` to use a different build interpreter or `-OutputDirectory <directory>` for another wheel output directory. Install each wheel using Python of the matching architecture.

### Install and try the local wheel

Use a separate test environment so the local build does not replace your installed tool. Run this in a fresh PowerShell session without `WINAPP_CLI_PATH`, so the tool uses the native CLI bundled in the wheel:

```powershell
python -m venv .\artifacts\python-local-test
$wheel = Get-ChildItem .\artifacts\python\winapp_cli-*-py3-none-win_amd64.whl |
  Sort-Object LastWriteTime -Descending | Select-Object -First 1
.\artifacts\python-local-test\Scripts\python.exe -m pip install --force-reinstall `
  $wheel.FullName
.\artifacts\python-local-test\Scripts\winapp-py.exe --help
.\artifacts\python-local-test\Scripts\winapp-py.exe --version
```

Use `win_arm64` instead of `win_amd64` when selecting an ARM64 wheel. After rebuilding the same version, rerun the install command: `--force-reinstall` replaces the earlier local build. Invoke this environment's `winapp-py.exe` when testing commands against a throwaway application project.

### Package an existing native publish

If the native CLI has already been built, omit `-BuildCli` and package its output without rebuilding:

```powershell
.\scripts\package-python.ps1 -Architecture x64
```

This consumes `artifacts\cli\win-x64`. Use `-CliPath <directory>` to consume publishes elsewhere. Without `-BuildCli` or `-Architecture`, the script preserves its CI behavior: it requires both `win-x64` and `win-arm64` publishes and packages both. Do not combine `-BuildCli` with `-CliPath`.

## Validate the wrapper

From the repository root, with the source environment active:

```powershell
python -m unittest discover -s .\src\winapp-python\tests -v
.\scripts\test-python.ps1 -WinappPath .\artifacts\cli\win-x64\winapp.exe
```

The script builds and installs a wrapper wheel, then validates SDK workflows, generated packages and fresh-clone dependency restoration in temporary projects copied from `tests\fixtures`. To consume an existing wheel without rebuilding it:

```powershell
$wheel = Get-ChildItem .\artifacts\python\winapp_cli-*-py3-none-win_amd64.whl |
  Sort-Object LastWriteTime -Descending | Select-Object -First 1
.\scripts\test-python.ps1 `
  -WheelPath $wheel.FullName `
  -IncludeIdentity -IncludeUv
```

`-IncludeIdentity` exercises the PBS runner and cleans its test registrations; Windows Developer Mode is required. `-IncludeUv` requires uv and covers declarations, lockfiles and fresh clones. The installed-wheel tests use the bundle rather than the editable installation's native path override.

To validate the user-facing WinUI workflow with Pester 5:

```powershell
.\scripts\test-samples.ps1 -Samples python-winui,python-identity -WinappPath .\artifacts\python
```
