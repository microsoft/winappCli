# Windows App Development CLI for Python

> **Status: Preview** - Python support is experimental.

The Windows App Development CLI manages Windows SDKs, app identity, manifests, certificates and Windows build tools. The Python package extends the CLI with generated WinRT bindings and a development identity runner.

This package gives you access to:

- **Windows APIs** - Generate Python bindings for Windows SDK and Windows App SDK WinRT APIs.
- **Project setup** - Restore SDKs and generate an installable bindings package for your application manager.
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

The package provides both the `winapp` command and `python -m winappcli`. See the guide's [prerequisites](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/python.md#prerequisites) to select the Python entry point when winget or npm is installed alongside it.

From your Python project directory, initialize SDKs and enable bindings:

```powershell
winapp init --use-defaults --add-python-bindings
```

The command prepares a temporary generator environment, records its version and generates an installable bindings package. It does not create or change your application `.venv`, dependencies or lockfiles. Install the generated package with your existing manager; see [dependency declarations](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/python.md#dependency-declarations) for pip, uv and portable Poetry examples. `--use-defaults` preserves existing binding selections and recorded versions.

Pass a project directory to target it explicitly. Interactive init without a directory asks you to confirm the bindings target from usable SDK configurations after native setup; see [initialize the project](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/python.md#2-initialize-the-project).

### Commands overview

**Project setup and bindings:**

| Command | What it does |
| --- | --- |
| [`init`](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/python.md#2-initialize-the-project) | Initialize SDKs and Windows project files; enable Python bindings with `--add-python-bindings`. |
| [`restore`](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/python.md#restore-and-update) | Restore pinned SDKs and the generator, then generate bindings before your manager installs application dependencies. |
| [`update`](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/python.md#restore-and-update) | Update SDK and generator pins, restore SDKs and regenerate bindings; synchronize application dependencies afterward. |
| [`generate`](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/python.md#configure-bindings) | Regenerate bindings from existing SDK metadata after changing type selections. |

**Run Python:**

For ordinary execution, use your application interpreter, package manager or IDE; see [run without identity](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/python.md#4-run-without-identity).

| Command | What it does |
| --- | --- |
| [`run --identity`](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/python.md#5-run-with-identity) | Prepare generated bindings in a private environment, optionally install `--requirements`, then run with package identity. |
| [`identity clear`](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/python.md#6-clean-up-development-identity) | Remove the development registration and private environment while preserving application data. |

For identity runs, preparation status goes to stderr. The script's stdout, stderr and exit code pass through unchanged, so `winapp run --identity .\app.py > output.txt` keeps wrapper status out of the output file. See [identity dependencies](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/python.md#identity-dependencies) for optional requirements, manual installation and recovery.

See [Choosing Python](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/python.md#choosing-python) for the default interpreter and `--python`, and [runtime catalog pinning](https://github.com/microsoft/WinAppCli/blob/main/docs/guides/python.md#runtime-catalog-pinning) for runtime availability and tool upgrades.

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

The [Python WinUI sample](https://github.com/microsoft/WinAppCli/blob/main/samples/python-winui/README.md) provides an in-memory counter. It uses the generated package's Bootstrap configuration for ordinary execution and skips Bootstrap when run with development identity.

## Development

To build and install a local tool wheel without publishing to PyPI, follow [Build and install a local wheel](https://github.com/microsoft/WinAppCli/blob/main/src/winapp-python/docs/development.md#build-and-install-a-local-wheel). Editable source setup and validation are covered in the same development guide.

## Support

See the [Support Guide](https://github.com/microsoft/WinAppCli/blob/main/SUPPORT.md) for issue templates and the triage process.

## Trademarks

This project may contain trademarks or logos for projects, products, or services. Authorized use of Microsoft trademarks or logos is subject to and must follow [Microsoft's Trademark & Brand Guidelines](https://www.microsoft.com/legal/intellectualproperty/trademarks/usage/general). Use of Microsoft trademarks or logos in modified versions of this project must not cause confusion or imply Microsoft sponsorship. Any use of third-party trademarks or logos are subject to those third-party's policies.
