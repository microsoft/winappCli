# Developing winapp for Python

This page covers source setup, building platform wheels and validating the Python wrapper. For application usage, follow the [Python guide](../../../docs/guides/python.md).

## Developing this package

Source development requires Windows, CPython 3.11-3.14, the .NET 10 SDK and Visual Studio C++ build tools. Prepare the complete native payload before installing the editable Python package. Run these commands from the repository root:

```powershell
python -m venv .\src\winapp-python\.venv
.\src\winapp-python\.venv\Scripts\Activate.ps1
$publish = Join-Path $PWD 'artifacts\python-dev\win-x64'
dotnet publish .\src\winapp-CLI\WinApp.Cli\WinApp.Cli.csproj -c Debug -r win-x64 `
  -p:PlatformTarget=x64 -p:PublishAot=false `
  -p:PublishSingleFile=false --self-contained -o $publish
if ($LASTEXITCODE -ne 0) { throw 'Native publish failed.' }

.\src\winapp-devtools\build-devtools.ps1 -Configuration Debug -Arch x64 `
  -EngineOut $publish -SkipNativeUnitTests
if ($LASTEXITCODE -ne 0) { throw 'DevTools engine build failed.' }

$native = Join-Path $PWD 'src\winapp-python\src\winappcli\_native'
if (Test-Path -LiteralPath $native) { Remove-Item -LiteralPath $native -Recurse -Force }
New-Item -ItemType Directory -Path $native -Force | Out-Null
Copy-Item "$publish\*" -Destination $native -Recurse

python -m pip install -e .\src\winapp-python
python -m winappcli --help
```

For ARM64 Python, use `win-arm64`, `-p:PlatformTarget=ARM64` and `-Arch arm64`. You can instead copy a complete matching publish from `artifacts\cli\win-x64` or `win-arm64` into `_native`.

Both editable installs and platform wheels use only `winappcli\_native\winapp.exe`. There are no command-line or environment overrides and no PATH search. Missing files or a mismatched architecture produce an error instead of using another installation. `_native` is ignored by Git.

Python source edits take effect immediately through the editable install. Rebuild and replace the whole `_native` directory only when native code changes; use an empty publish directory when changing architectures or publish settings. Do not copy `winapp.exe` alone: its DLLs, engines and other publish files belong to the same build.

## Build and install a local wheel

To build a local `microsoft-winappcli` wheel, run one command from the repository root:

```powershell
.\scripts\package-python.ps1 -BuildCli
```

Windows CPython 3.11-3.14, PowerShell 7, the .NET 10 SDK and Visual Studio C++ build tools for the target architecture are required. If publishing reports a missing platform linker, install the C++ tools for that architecture before retrying.

The script selects the architecture of `python` by default, builds a fresh Release native CLI and DevTools engines, then packages the complete payload with the Python wrapper. It includes the license, omits PDBs and checks required files, version and executable architecture. Native publishing uses a temporary directory that is removed afterward; existing native publishes and unrelated artifacts are not deleted.

The wheel is written to `artifacts\python`. Its version uses the same repository `version.json`, build number and prerelease label as native and npm packages. For example, shared version `0.7.2-prerelease.37` produces Python version `0.7.2.dev37` and an x64 wheel named `microsoft_winappcli-0.7.2.dev37-py3-none-win_amd64.whl`; ARM64 uses the corresponding `win_arm64` tag. Source `pyproject.toml` keeps its editable-development version; only the staged copy receives the build version. The script does not upload to PyPI, install or replace your tool, or package the counter application.

Use `-Stable` for the base release version, or `-Version <shared-build-version>` to consume a version already selected by the build. Existing native publishes must match that shared version; rebuild them or provide their correct `-Version` if packaging reports a mismatch.

### Select another architecture or interpreter

Use `-Architecture x64`, `-Architecture arm64` or `-Architecture All` to select targets explicitly. Building both architectures requires both sets of C++ tools:

```powershell
.\scripts\package-python.ps1 -BuildCli -Architecture All
```

Pass `-Python <python.exe>` to use a different build interpreter or `-OutputDirectory <directory>` for another wheel output directory. Install each wheel using Python of the matching architecture.

### Install and try the local wheel

Use a separate test environment so the local build does not replace your installed tool. The installed package always uses its bundled native CLI:

```powershell
python -m venv .\artifacts\python-local-test
$wheel = Get-ChildItem .\artifacts\python\microsoft_winappcli-*-py3-none-win_amd64.whl |
  Sort-Object LastWriteTime -Descending | Select-Object -First 1
.\artifacts\python-local-test\Scripts\python.exe -m pip install --force-reinstall `
  $wheel.FullName
.\artifacts\python-local-test\Scripts\winapp.exe --help
.\artifacts\python-local-test\Scripts\winapp.exe --version
```

Use `win_arm64` instead of `win_amd64` when selecting an ARM64 wheel. After rebuilding the same version, rerun the install command: `--force-reinstall` replaces the earlier local build. Invoke this environment's `winapp.exe` when testing commands against a throwaway application project.

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
$wheel = Get-ChildItem .\artifacts\python\microsoft_winappcli-*-py3-none-win_amd64.whl |
  Sort-Object LastWriteTime -Descending | Select-Object -First 1
.\scripts\test-python.ps1 `
  -WheelPath $wheel.FullName `
  -IncludeIdentity -IncludeUv
```

`-IncludeIdentity` exercises the PBS runner and cleans its test registrations; Windows Developer Mode is required. `-IncludeUv` requires uv and covers declarations, lockfiles and fresh clones. Both source and installed-wheel tests use the same bundled-CLI lookup.

To validate the user-facing WinUI workflow with Pester 5:

```powershell
.\scripts\test-samples.ps1 -Samples python-winui,python-identity -WinappPath .\artifacts\python
```

## Update the identity runtime catalog

The wheel pins its catalog in [`python_runtime.py`](../src/winappcli/python_runtime.py). To refresh it, choose an official stable uv release, resolve its tag to a full commit, and download `crates/uv-python-managed/download-metadata.json` at that commit. Compute the SHA-256 of the original file, without reformatting or filtering it:

```powershell
Get-FileHash .\download-metadata.json -Algorithm SHA256
```

Update `CATALOG_COMMIT`, `CATALOG_SHA256` and the release comment together. Check the exact Python versions and x64/ARM64 entries needed by the samples, then run the Python unit tests and installed-wheel identity workflow above. Catalog updates are deliberate source changes; wheel builds do not select the latest uv release automatically.
