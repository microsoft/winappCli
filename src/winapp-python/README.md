# Windows App Development CLI for Python

> **Status: Preview** - Python support is experimental.

The Windows App Development CLI manages Windows SDKs, app identity, manifests, certificates and Windows build tools. The Python package extends the CLI with generated WinRT bindings, project dependency setup and a development identity runner.

This package gives you access to:

- **Windows APIs** - Generate Python bindings for Windows SDK and Windows App SDK WinRT APIs.
- **Project setup** - Restore SDKs and declared Python dependencies into a project virtual environment.
- **Development identity** - Run Python scripts with Windows package identity without changing the ordinary development environment.
- **Windows tools** - Use the native CLI's manifest, certificate, signing and SDK tool commands.

## Get started

Follow the [Python guide](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/python.md) to call a Windows API from a console script, compare ordinary and identity-backed execution, and prepare an MSIX.

## Usage

Use Windows CPython 3.11-3.14, x64 or ARM64. Install the tool from PyPI:

```powershell
python -m pip install --upgrade microsoft-winappcli
winapp --help
```

pip selects the package matching your Python architecture. It includes the native CLI; no separate winapp or Node.js installation is needed. You can also use `uv tool install microsoft-winappcli` or `pipx install microsoft-winappcli` for an isolated tool environment.

The package provides both the `winapp` command and `python -m winapp`. See the guide's [prerequisites](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/python.md#prerequisites) to select the Python entry point when winget or npm is installed alongside it.

From your Python project directory, initialize SDKs and enable bindings:

```powershell
winapp init --use-defaults --add-python-bindings
```

The command creates or reuses a normal project `.venv`, records the generator version in project configuration, generates bindings and installs declared dependencies there. `--use-defaults` preserves existing binding selections and recorded versions; resetting API selections requires interactive confirmation. Application dependencies are separate from the tool's installation environment. See the guide's [DynWinRT and application dependency handling](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/python.md#dependency-declarations) for version upgrades.

Pass a project directory to target it explicitly. Interactive init without a directory asks you to confirm the bindings target from usable SDK configurations after native setup; see [initialize the project](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/python.md#2-initialize-the-project).

### Commands overview

**Project setup and bindings:**

| Command | What it does |
| --- | --- |
| [`init`](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/python.md#2-initialize-the-project) | Initialize SDKs and Windows project files; enable Python bindings with `--add-python-bindings`. |
| [`restore`](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/python.md#restore-and-update) | Run native SDK and projection restoration, then generate configured bindings and install declared dependencies. |
| [`update`](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/python.md#restore-and-update) | Update SDK and generator pins, restore SDKs and projections, then regenerate configured bindings and install dependencies. |
| [`generate`](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/python.md#configure-bindings) | Regenerate bindings from existing SDK metadata after changing type selections. |

**Run Python:**

| Command | What it does |
| --- | --- |
| [`run`](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/python.md#4-run-without-identity) | Run a project-local `.py` script with the existing project `.venv`; other inputs are forwarded to native `run`. |
| [`run --identity`](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/python.md#5-run-with-identity) | Run a script in a private Python environment with the project's package identity. |
| [`identity clear`](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/python.md#6-clean-up-development-identity) | Remove the development registration and private environment while preserving application data. |

For Python scripts, interpreter selection and identity-preparation status go to stderr. The script's stdout, stderr and exit code pass through unchanged, so `winapp run .\app.py > output.json` keeps wrapper status out of the output file.

See [runtime catalog pinning](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/python.md#runtime-catalog-pinning) for the project setting that controls identity runtime selection and updates.

**Shared Windows tools:**

| Command | What it does |
| --- | --- |
| [`manifest`](https://github.com/microsoft/WinAppCli/blob/main/docs/usage.md#manifest) | Generate and manage AppxManifest files and assets. |
| [`cert`](https://github.com/microsoft/WinAppCli/blob/main/docs/usage.md#cert) | Generate, inspect and install development certificates. |
| [`sign`](https://github.com/microsoft/WinAppCli/blob/main/docs/usage.md#sign) / [`az-sign`](https://github.com/microsoft/WinAppCli/blob/main/docs/usage.md#az-sign) | Sign packages and executables. |
| [`tool`](https://github.com/microsoft/WinAppCli/blob/main/docs/usage.md#tool) | Run Windows SDK tools. |
| [`get-winapp-path`](https://github.com/microsoft/WinAppCli/blob/main/docs/usage.md#get-winapp-path) | Find installed SDK components. |
| [`package`](https://github.com/microsoft/WinAppCli/blob/main/docs/usage.md#pack) | Create an MSIX from an already prepared directory. |

These shared commands forward to the native CLI. For example:

```powershell
winapp manifest --help
winapp tool --help
```

`restore --help` and `update --help` show Python workflow options; `run --help` separates Python scripts from native inputs such as executables and .NET projects.

See the guide's [output options](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/python.md#output-options) for quiet and verbose modes and passing arguments to scripts.

See the [CLI reference](https://github.com/microsoft/WinAppCli/blob/main/docs/usage.md) for the complete native command list. The Python preview does not automatically bundle a Python application for production MSIX distribution.

## Feedback

- [File an issue, feature request or bug](https://github.com/microsoft/WinAppCli/issues); check for an existing report first.
- Send feedback to <windowsdevelopertoolkit@microsoft.com>.

## Samples

The [Python identity sample](https://github.com/microsoft/WinAppCli/blob/main/samples/python-identity/README.md) demonstrates ordinary and identity-backed console execution, cleanup and manual PyInstaller/MSIX packaging.

The [Python WinUI sample](https://github.com/microsoft/WinAppCli/blob/main/samples/python-winui/README.md) provides an in-memory counter and a file picker. Both run unpackaged using Bootstrap, without development identity.

## Development

To build and install a local tool wheel without publishing to PyPI, follow [Build and install a local wheel](https://github.com/microsoft/WinAppCli/blob/main/src/winapp-python/docs/development.md#build-and-install-a-local-wheel). Editable source setup and validation are covered in the same development guide.

## Support

See the [Support Guide](https://github.com/microsoft/WinAppCli/blob/main/SUPPORT.md) for issue templates and the triage process.

## Trademarks

This project may contain trademarks or logos for projects, products, or services. Authorized use of Microsoft trademarks or logos is subject to and must follow [Microsoft's Trademark & Brand Guidelines](https://www.microsoft.com/legal/intellectualproperty/trademarks/usage/general). Use of Microsoft trademarks or logos in modified versions of this project must not cause confusion or imply Microsoft sponsorship. Any use of third-party trademarks or logos are subject to those third-party's policies.
