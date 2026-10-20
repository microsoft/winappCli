# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

"""Opt-in development identity backed by a project-owned PBS runtime."""

from contextlib import contextmanager, nullcontext, redirect_stdout
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import uuid
import xml.etree.ElementTree as ET

from .errors import WinappError
from .identity_environment import application_environment, dependency_inputs, synchronize_dependencies
from .paths import check_output_tree, local_path
from .progress import Workflow
from .python_runtime import (
    distribution_from_document, prepare_runtime, probe_interpreter,
    resolve_distribution, select_project_python,
)
from .tools import run_tool

RECORD_NAME = "python-identity.json"
PRIVATE_MARKER = ".winapp-owned.json"
ROOT_NAME = "python-identity"
APPX_NS = "{http://schemas.microsoft.com/appx/manifest/foundation/windows10}"
QUERY_PACKAGES = """
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$packages = @(Get-AppxPackage -Name $env:WINAPP_PY_PACKAGE_NAME |
    Where-Object { $_.Name -eq $env:WINAPP_PY_PACKAGE_NAME } |
    Select-Object Name, PackageFullName, Publisher, InstallLocation, IsDevelopmentMode)
ConvertTo-Json -InputObject $packages -Compress -Depth 4
"""
REMOVE_PACKAGE = """
$ErrorActionPreference = 'Stop'
Remove-AppxPackage -Package $env:WINAPP_PY_PACKAGE_FULL_NAME -PreserveApplicationData -Confirm:$false
"""
PROBE_IDENTITY = """
import ctypes
import json
import sys, sysconfig
from ctypes import wintypes

get_name = ctypes.WinDLL("kernel32", use_last_error=True).GetCurrentPackageFullName
get_name.argtypes = [ctypes.POINTER(wintypes.UINT), wintypes.LPWSTR]
get_name.restype = wintypes.LONG
get_image = ctypes.WinDLL("kernel32", use_last_error=True).GetModuleFileNameW
get_image.argtypes = [wintypes.HMODULE, wintypes.LPWSTR, wintypes.DWORD]
get_image.restype = wintypes.DWORD
length = wintypes.UINT()
code = get_name(ctypes.byref(length), None)
name = None
if code == 122:
    buffer = ctypes.create_unicode_buffer(length.value)
    code = get_name(ctypes.byref(length), buffer)
    name = buffer.value
image = ctypes.create_unicode_buffer(32768)
if not get_image(None, image, len(image)):
    raise ctypes.WinError(ctypes.get_last_error())
print(json.dumps({"code": code, "name": name, "prefix": sys.prefix,
    "base_prefix": sys.base_prefix, "image": image.value,
    "version": sys.version.split()[0], "platform": sysconfig.get_platform(),
    "free_threaded": bool(sysconfig.get_config_var("Py_GIL_DISABLED")),
    "debug": bool(sysconfig.get_config_var("Py_DEBUG"))}))
"""


def _digest(path: Path) -> str:
    hasher = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            hasher.update(chunk)
    return hasher.hexdigest()


def _record_path(project: Path) -> Path:
    return local_path(project / ".winapp" / RECORD_NAME, boundary=project)


def _load_record(project: Path) -> dict | None:
    source = _record_path(project)
    if not source.exists():
        return None
    try:
        record = json.loads(source.read_text(encoding="utf-8"))
    except (json.JSONDecodeError, UnicodeError) as error:
        raise WinappError(f"Invalid Python identity ownership record at {source}: {error}") from error
    required = {"schema", "project", "root", "owner", "state", "entry", "base",
                "package", "publisher", "full_name", "interpreter", "distribution",
                "manifest_hash", "dependency_hash", "runtime_hashes", "registration_attempted"}
    if not isinstance(record, dict) or not required <= record.keys() or record.get("schema") != 2:
        raise WinappError(
            f"Unrecognized Python identity ownership record at {source}; refusing to modify it."
        )
    if any(not isinstance(record[key], str) or not record[key] for key in
           ("project", "root", "owner", "state", "entry", "base", "package", "publisher",
            "manifest_hash", "dependency_hash")):
        raise WinappError(f"The Python identity record has invalid paths or package identifiers: {source}")
    if (
        record["state"] not in ("preparing", "ready")
        or not re.fullmatch(r"[a-f0-9]{32}", record["owner"])
        or not isinstance(record["interpreter"], dict) or not isinstance(record["distribution"], dict)
        or not isinstance(record["runtime_hashes"], dict)
        or type(record["registration_attempted"]) is not bool
        or any(not isinstance(key, str) or not isinstance(value, str)
               for key, value in record["runtime_hashes"].items())
        or (record["full_name"] is not None and not isinstance(record["full_name"], str))
        or (record["state"] == "ready" and not record["full_name"])
    ):
        raise WinappError(f"The Python identity record has invalid runtime or preparation state: {source}")
    if record["project"].lower() != str(project).lower():
        raise WinappError(f"{source} belongs to a different project; refusing to modify it.")
    _owned_directory(project, record)
    return record


def _write_record(project: Path, record: dict) -> None:
    destination = _record_path(project)
    destination.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(prefix=".python-identity-", suffix=".json",
                                     dir=destination.parent, delete=False) as staging:
        staged = Path(staging.name)
        staging.write(json.dumps(record, indent=2).encode("utf-8") + b"\n")
    try:
        os.replace(staged, destination)
    finally:
        staged.unlink(missing_ok=True)


def _powershell(script: str, *, name: str | None = None, full_name: str | None = None) -> str:
    system_root = Path(os.environ.get("SystemRoot", r"C:\Windows"))
    executable = system_root / "System32" / "WindowsPowerShell" / "v1.0" / "powershell.exe"
    if not executable.is_file():
        raise WinappError(f"Windows PowerShell is needed for package ownership checks: {executable}")
    environment = {**os.environ}
    if name is not None:
        environment["WINAPP_PY_PACKAGE_NAME"] = name
    if full_name is not None:
        environment["WINAPP_PY_PACKAGE_FULL_NAME"] = full_name
    try:
        completed = subprocess.run(
            [str(executable), "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script],
            capture_output=True, text=True, encoding="utf-8", errors="replace",
            env=environment, timeout=60, check=False,
        )
    except (OSError, subprocess.TimeoutExpired) as error:
        raise WinappError(f"Could not query or update Windows package registration: {error}") from error
    if completed.returncode:
        raise WinappError(
            f"Windows package operation failed (exit {completed.returncode}): "
            f"{(completed.stderr or completed.stdout).strip()}"
        )
    return completed.stdout.strip()


def _packages(name: str) -> list[dict]:
    output = _powershell(QUERY_PACKAGES, name=name)
    if not output:
        raise WinappError(f"Could not read Windows package registrations for {name}.")
    try:
        packages = json.loads(output)
    except json.JSONDecodeError as error:
        raise WinappError(f"Windows returned invalid registration information for {name}: {output}") from error
    if not isinstance(packages, list) or any(not isinstance(package, dict) for package in packages):
        raise WinappError(f"Unexpected Windows package registration format for {name}.")
    return packages


def _owned_package(record: dict, packages: list[dict]) -> dict | None:
    matches = [item for item in packages if item.get("PackageFullName") == record["full_name"]]
    if len(matches) > 1:
        raise WinappError("Windows returned duplicate registrations for the project's package.")
    if not matches:
        return None
    package = matches[0]
    if (
        not isinstance(package.get("Name"), str) or not isinstance(package.get("Publisher"), str)
        or package["Name"].lower() != record["package"].lower()
        or package["Publisher"].lower() != record["publisher"].lower()
        or package.get("IsDevelopmentMode") is not True
    ):
        raise WinappError("The recorded identity no longer matches a development package; refusing to remove it.")
    location = package.get("InstallLocation")
    if not isinstance(location, str) or not location or not local_path(Path(location)).is_relative_to(record["project"]):
        raise WinappError("The recorded identity is installed outside this project; refusing to remove it.")
    return package


def _owned_directory(project: Path, record: dict) -> Path:
    private = local_path(project / ".winapp" / ROOT_NAME, boundary=project)
    if Path(record["root"]) != private:
        raise WinappError("The Python identity record points outside its project-owned PBS environment.")
    if (
        Path(record["base"]) not in {private / "venv" / "runtime" / name for name in ("python.exe", "python_d.exe")}
        or Path(record["entry"]) not in {private / "venv" / "Scripts" / name for name in ("python.exe", "python_d.exe")}
    ):
        raise WinappError("The Python identity record points outside the owned runtime or launcher.")
    if private.exists():
        marker = local_path(private / PRIVATE_MARKER, boundary=project)
        try:
            owner = json.loads(marker.read_text(encoding="utf-8"))
        except (FileNotFoundError, json.JSONDecodeError, UnicodeError) as error:
            raise WinappError(f"Cannot verify the private interpreter's ownership: {private}") from error
        if owner != {"schema": 2, "project": str(project), "owner": record["owner"]}:
            raise WinappError(f"The private PBS environment belongs to another project: {private}")
    return private


def _remove_owned(record: dict) -> None:
    if record["full_name"] is None and not record["registration_attempted"]:
        return
    with _registration_lock(record["package"]):
        packages = _packages(record["package"])
        if record["full_name"] is None:
            if not packages:
                return
            if len(packages) != 1:
                raise WinappError("Cannot verify a unique registration for the partially prepared identity.")
            full_name = packages[0].get("PackageFullName")
            if not isinstance(full_name, str) or not full_name:
                raise WinappError("Windows returned no full identity for the partially prepared package.")
            record = {**record, "full_name": full_name}
        if _owned_package(record, packages):
            _powershell(REMOVE_PACKAGE, full_name=record["full_name"])
            if _packages(record["package"]):
                raise WinappError(f"A registration remains for {record['package']}; no files were removed.")
        elif packages:
            raise WinappError("A different registration now uses this identity; refusing to clear its private environment.")


def _manifest_identity(manifest: Path) -> tuple[str, str]:
    try:
        root = ET.parse(manifest).getroot()
    except ET.ParseError as error:
        raise WinappError(f"Invalid appxmanifest at {manifest}: {error}") from error
    identity = root.find(f"{APPX_NS}Identity")
    apps = root.findall(f"{APPX_NS}Applications/{APPX_NS}Application")
    if identity is None or len(apps) != 1:
        raise WinappError("Python identity requires a manifest with one Identity and one Application.")
    name, publisher = identity.get("Name"), identity.get("Publisher")
    if not name or not publisher or not apps[0].get("Id"):
        raise WinappError("The manifest must declare an Identity Name, Publisher, and Application Id.")
    if any(re.search(r"\$[A-Za-z_][A-Za-z0-9_]*\$", value) for value in (name, publisher)):
        raise WinappError(
            "Python identity requires literal Identity Name and Publisher values. "
            "Replace their manifest placeholders before running --identity."
        )
    return (name if name.endswith(".debug") else name + ".debug"), publisher


def _require_developer_mode() -> None:
    import winreg

    try:
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE,
                            r"SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock") as key:
            enabled, _ = winreg.QueryValueEx(key, "AllowDevelopmentWithoutDevLicense")
    except FileNotFoundError:
        enabled = 0
    except OSError as error:
        raise WinappError(f"Could not check Windows Developer Mode: {error}") from error
    if enabled != 1:
        raise WinappError("Windows Developer Mode is required for development identity. Enable it in Windows Settings and retry; no machine setting was changed.")


@contextmanager
def _file_lock(path: Path, message: str):
    import msvcrt

    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("a+b") as stream:
        if stream.seek(0, os.SEEK_END) == 0:
            stream.write(b"\0")
            stream.flush()
        stream.seek(0)
        try:
            msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
        except OSError as error:
            raise WinappError(message) from error
        try:
            yield
        finally:
            stream.seek(0)
            msvcrt.locking(stream.fileno(), msvcrt.LK_UNLCK, 1)


def _operation_lock(project: Path):
    path = local_path(project / ".winapp" / ".python-identity.lock", boundary=project)
    return _file_lock(path, "This project's identity is in use by another run or preparation. Wait for it to exit before retrying.")


def _registration_lock(package_name: str):
    home = local_path(Path.home())
    key = hashlib.sha256(package_name.casefold().encode("utf-8")).hexdigest()
    path = local_path(home / ".winapp" / "state" / "python-identity-locks" / f"{key}.lock", boundary=home)
    return _file_lock(path, f"{package_name} is being updated by another identity operation. Wait for it to finish before retrying.")


def _runtime_hashes(record: dict) -> dict[str, str]:
    root = Path(record["root"])
    runtime = root / "venv" / "runtime"
    files = [Path(record["base"]), Path(record["entry"]), root / "venv" / "pyvenv.cfg",
             *sorted(runtime.glob("*.dll"))]
    if any(not file.is_file() for file in files):
        raise WinappError("The private runtime or identity venv is missing. Run 'winapp identity clear' and prepare it again.")
    return {str(file.relative_to(root)): _digest(local_path(file, boundary=root)) for file in files}


def _verify_interpreter(project: Path, record: dict) -> None:
    root = Path(record["root"])
    venv = root / "venv"
    try:
        result = subprocess.run(
            [record["entry"], "-I", "-c", PROBE_IDENTITY], cwd=project,
            capture_output=True, text=True, encoding="utf-8", errors="replace", check=False,
            env=application_environment(venv), timeout=30,
        )
    except (OSError, subprocess.TimeoutExpired) as error:
        raise WinappError(f"Cannot run the private PBS interpreter: {error}") from error
    if result.returncode:
        raise WinappError(f"Private Python identity check failed: {(result.stderr or result.stdout).strip()}")
    try:
        state = json.loads(result.stdout.strip())
    except json.JSONDecodeError as error:
        raise WinappError(f"Private Python returned invalid identity information: {result.stdout}") from error
    if not isinstance(state, dict) or any(
        not isinstance(state.get(key), str)
        for key in ("prefix", "base_prefix", "image", "version", "platform")
    ):
        raise WinappError("Private Python returned an invalid identity state.")
    info = record["interpreter"]
    platforms = {"x86_64": "win-amd64", "aarch64": "win-arm64", "i686": "win32"}
    if (
        state.get("code") != 0 or state.get("name") != record["full_name"]
        or Path(state.get("prefix", "")) != venv
        or Path(state.get("base_prefix", "")) != venv / "runtime"
        or Path(state.get("image", "")) != Path(record["base"])
        or state.get("version") != info["version"]
        or state.get("platform") != platforms.get(info["architecture"])
        or state.get("free_threaded") != info["free_threaded"] or state.get("debug") != info["debug"]
    ):
        raise WinappError(
            f"Private Python has the wrong identity or environment: "
            f"package={state.get('name')}, code={state.get('code')}, "
            f"prefix={state.get('prefix')}, base={state.get('base_prefix')}, image={state.get('image')}."
        )


def _manifest_path(project: Path, selected: Path | None) -> Path:
    if selected is not None:
        path = local_path(project / selected, boundary=project)
        if not path.is_file():
            raise WinappError(f"Package manifest does not exist: {path}")
        return path
    for name in ("Package.appxmanifest", "appxmanifest.xml"):
        path = local_path(project / name, boundary=project)
        if path.is_file():
            return path
    raise WinappError(
        "Package.appxmanifest or appxmanifest.xml is required for --identity. "
        "Create an app manifest first with 'winapp manifest generate'."
    )


def _clear_owned_files(project: Path, record: dict) -> None:
    root = _owned_directory(project, record)
    if root.exists():
        check_output_tree(root)
    _remove_owned(record)
    if root.exists():
        shutil.rmtree(root)
    _record_path(project).unlink(missing_ok=True)


def run_with_identity(
    project: Path, script: Path, script_args: list[str], native: Path,
    manifest_option: Path | None, *, python_option: Path | None = None,
    requirements_option: Path | None = None, verbose: bool = False, quiet: bool = False,
) -> int:
    if not project.is_dir():
        raise WinappError(f"Project directory does not exist: {project}")
    script = local_path(project / script, boundary=project)
    if script.suffix.lower() != ".py" or not script.is_file():
        raise WinappError(f"An existing project-local .py script is required: {script}")
    manifest = _manifest_path(project, manifest_option)
    package_name, publisher = _manifest_identity(manifest)
    project_python = select_project_python(project, python_option)
    interpreter = probe_interpreter(project_python, project)
    requirements, bindings, dependency_hash = dependency_inputs(project, requirements_option, interpreter)
    manifest_hash = _digest(manifest)
    with _operation_lock(project):
        record = _load_record(project)
        changes: list[str] = []
        interpreter_changed = False
        if record is not None:
            if record["state"] != "ready":
                raise WinappError("Identity preparation is incomplete. Run 'winapp identity clear' and retry.")
            if _runtime_hashes(record) != record["runtime_hashes"]:
                raise WinappError("The owned PBS runtime or identity venv changed. Run 'winapp identity clear' and prepare it again.")
            if _owned_package(record, _packages(record["package"])) is None:
                raise WinappError("The recorded development package is missing. Run 'winapp identity clear' and prepare it again.")
            interpreter_changed = record["interpreter"] != interpreter.document()
            if interpreter_changed:
                changes.append("project Python")
            if (
                record["manifest_hash"] != manifest_hash
                or record["package"] != package_name or record["publisher"] != publisher
            ):
                changes.append("package manifest")
            if changes and package_name != record["package"] and _packages(package_name):
                raise WinappError(f"{package_name} is already registered without this project's ownership record; refusing to replace it.")
        else:
            root = local_path(project / ".winapp" / ROOT_NAME, boundary=project)
            if root.exists():
                raise WinappError(f"Private identity directory exists without an ownership record: {root}")
            if _packages(package_name):
                raise WinappError(f"{package_name} is already registered without this project's ownership record; refusing to replace it.")
            _require_developer_mode()

        if record is None or interpreter_changed:
            distribution = resolve_distribution(interpreter)
        else:
            distribution = distribution_from_document(record["distribution"], interpreter)
        if record is not None and record["dependency_hash"] != dependency_hash:
            changes.append("dependencies or project metadata")
        action = "Rebuilding" if changes else "Reusing" if record is not None else "Preparing"
        architecture = {"x86_64": "x64", "aarch64": "ARM64", "i686": "x86"}[interpreter.architecture]
        python_label = f"Python: {interpreter.version} ({architecture})"
        with (
            open(os.devnull, "w", encoding="utf-8") if quiet else nullcontext(sys.stderr)
        ) as diagnostics, redirect_stdout(diagnostics), Workflow(f"{action} development identity", verbose=verbose) as workflow:
            if changes:
                print(f"  Changed: {', '.join(changes)}", flush=True)
                _require_developer_mode()
                with workflow.step("Reset owned identity", "Owned identity reset; app data preserved"):
                    _clear_owned_files(project, record)
                record = None
            if record is None:
                root = local_path(project / ".winapp" / ROOT_NAME, boundary=project)
                if root.exists():
                    raise WinappError(f"Private identity directory exists without an ownership record: {root}")
                if changes and _packages(package_name):
                    raise WinappError(f"{package_name} is already registered without this project's ownership record; refusing to replace it.")
                record = {
                    "schema": 2, "project": str(project), "root": str(root), "owner": uuid.uuid4().hex,
                    "state": "preparing", "entry": str(root / "venv" / "Scripts" / "python.exe"),
                    "base": str(root / "venv" / "runtime" / "python.exe"),
                    "package": package_name, "publisher": publisher, "full_name": None,
                    "interpreter": interpreter.document(), "distribution": distribution.document(),
                    "manifest_hash": manifest_hash, "dependency_hash": dependency_hash, "runtime_hashes": {},
                    "registration_attempted": False,
                }
                root.mkdir()
                try:
                    (root / PRIVATE_MARKER).write_text(json.dumps({
                        "schema": 2, "project": str(project), "owner": record["owner"],
                    }) + "\n", encoding="utf-8")
                except OSError:
                    (root / PRIVATE_MARKER).unlink(missing_ok=True)
                    root.rmdir()
                    raise
                try:
                    _write_record(project, record)
                    with workflow.step("Prepare private Python", f"Private environment ready; {python_label}"):
                        venv, runtime, entry = prepare_runtime(project, root, distribution, verbose=verbose)
                        record["entry"] = str(entry)
                        record["base"] = str(runtime / ("python_d.exe" if not (runtime / "python.exe").is_file() else "python.exe"))
                        _write_record(project, record)
                    with workflow.step("Install dependencies", "Application dependencies installed"):
                        synchronize_dependencies(project, entry, venv, requirements, bindings, verbose=verbose)
                        if (
                            _digest(manifest) != manifest_hash
                            or dependency_inputs(project, requirements_option, interpreter)[2] != dependency_hash
                        ):
                            raise WinappError("Project inputs changed during identity preparation; retry with unchanged inputs.")
                    with _registration_lock(package_name):
                        with workflow.step("Register identity", "Package identity registered"):
                            if _packages(package_name):
                                raise WinappError("The package identity was registered by another operation during preparation; refusing to replace it.")
                            record["registration_attempted"] = True
                            _write_record(project, record)
                            run_tool([str(native), "create-debug-identity", record["base"], "--manifest", str(manifest)],
                                     project, verbose=verbose)
                            registrations = _packages(package_name)
                            if len(registrations) != 1:
                                raise WinappError(f"Expected one development registration for {package_name}.")
                            full_name = registrations[0].get("PackageFullName")
                            if not isinstance(full_name, str) or not full_name:
                                raise WinappError("Windows returned no complete package identity.")
                            record["full_name"] = full_name
                            _owned_package(record, registrations)
                        with workflow.step("Verify runtime", "Runtime verified"):
                            record["runtime_hashes"] = _runtime_hashes(record)
                            _verify_interpreter(project, record)
                            record["state"] = "ready"
                            _write_record(project, record)
                except (WinappError, OSError, KeyboardInterrupt) as error:
                    try:
                        _clear_owned_files(project, record)
                    except (WinappError, OSError) as cleanup_error:
                        raise WinappError(
                            f"Identity preparation failed: {error}. Cleanup could not finish: {cleanup_error}. "
                            "The ownership record was preserved; run 'winapp identity clear' before retrying."
                        ) from error
                    raise
            else:
                with workflow.step("Verify identity", "Existing runtime verified"):
                    _verify_interpreter(project, record)
            workflow.succeed("Identity ready.")
        if not quiet:
            print(f"Running {script.name}...", file=sys.stderr, flush=True)
        try:
            return subprocess.run(
                [record["entry"], str(script), *script_args], cwd=project, check=False,
                env=application_environment(Path(record["root"]) / "venv"),
            ).returncode
        except OSError as error:
            raise WinappError(f"Could not launch the script in the PBS identity environment: {error}") from error


def clear_identity(project: Path) -> int:
    with _operation_lock(project):
        record = _load_record(project)
        if record is None:
            raise WinappError(f"No Python identity ownership record found in {project / '.winapp'}.")
        _clear_owned_files(project, record)
    print("Project Python identity unregistered; application data was preserved.", flush=True)
    return 0
