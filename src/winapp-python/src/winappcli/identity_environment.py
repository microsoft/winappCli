# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

from dataclasses import dataclass
import hashlib
import json
import os
from pathlib import Path
import sys
import tempfile
from urllib.parse import urlparse
from urllib.request import url2pathname

from packaging.utils import canonicalize_name

from .config import try_load_config
from .dependencies import installer_environment, pip_command, requirements_files, validate_project_python
from .errors import WinappError
from .generate import require_current_package
from .paths import local_path
from .python_runtime import Interpreter, clean_python_environment
from .tools import run_tool

SNAPSHOT = ".winapp-dependencies.json"
REPORT = ".winapp-install-report.json"


@dataclass(frozen=True)
class DependencyPlan:
    bindings: Path | None
    fingerprint: str
    requirements: Path | None = None
    managed: tuple[str, ...] = ()


def application_environment(venv: Path) -> dict[str, str]:
    environment = clean_python_environment()
    environment["VIRTUAL_ENV"] = str(venv)
    environment["PATH"] = str(venv / "Scripts") + os.pathsep + environment.get("PATH", "")
    return environment


def dependency_inputs(
    project: Path, selected: Path | None, interpreter: Interpreter,
) -> DependencyPlan:
    validate_project_python(project, interpreter)
    config = try_load_config(project)
    bindings = require_current_package(config) if config is not None else None
    managed = (canonicalize_name(config.package), "dynwinrt") if config else ()
    files: list[Path] = []
    requirements = None
    if selected is not None:
        requirements = local_path(project / selected, boundary=project)
        files.extend(requirements_files(project, requirements))
        # Explicit requirements can install the application itself with "-e .".
        pyproject = local_path(project / "pyproject.toml", boundary=project)
        if pyproject.is_file():
            files.append(pyproject)
    if bindings is not None:
        files.append(bindings / "pyproject.toml")
    digest = hashlib.sha256(json.dumps({
        "requirements": str(requirements) if requirements else None,
        "bindings": str(bindings) if bindings else None, "managed": managed,
    }, sort_keys=True).encode())
    for file in sorted(set(files), key=str):
        digest.update(str(file).encode())
        digest.update(file.read_bytes())
    return DependencyPlan(bindings, digest.hexdigest(), requirements, managed)


def _read_json(text: str, description: str) -> dict:
    try:
        document = json.loads(text)
    except (ValueError, UnicodeError) as error:
        raise WinappError(f"Invalid {description}: {error}") from error
    if not isinstance(document, dict):
        raise WinappError(f"Invalid {description}: expected an object.")
    return document


def _write_json(path: Path, document: dict) -> None:
    local_path(path)
    with tempfile.NamedTemporaryFile(dir=path.parent, prefix=".dependencies-", delete=False) as stream:
        staged = Path(stream.name)
        stream.write(json.dumps(document, indent=2).encode("utf-8") + b"\n")
    try:
        os.replace(staged, path)
    finally:
        staged.unlink(missing_ok=True)


def capture_dependencies(project: Path, entry: Path, plan: DependencyPlan) -> dict:
    environment = clean_python_environment()
    inspected = _read_json(
        run_tool(pip_command(entry, "inspect", "--local"), project, environment=environment),
        "pip environment inspection",
    )
    if inspected.get("version") != "1" or not isinstance(inspected.get("installed"), list):
        raise WinappError("Unsupported pip environment inspection format.")
    packages = []
    for item in inspected["installed"]:
        metadata = item.get("metadata") if isinstance(item, dict) else None
        if not isinstance(metadata, dict) or any(
            not isinstance(metadata.get(key), str) or not metadata[key] for key in ("name", "version")
        ):
            raise WinappError("pip inspection returned invalid installed-package metadata.")
        origin = item.get("direct_url")
        if origin is not None and not isinstance(origin, dict):
            raise WinappError("pip inspection returned invalid installed-package source metadata.")
        packages.append({
            "name": canonicalize_name(metadata["name"]), "version": metadata["version"],
            "direct_url": origin,
        })
    if len({item["name"] for item in packages}) != len(packages):
        raise WinappError("The identity environment contains duplicate package metadata.")
    venv = entry.parent.parent
    previous_path = local_path(venv / SNAPSHOT, boundary=venv)
    managed = set(plan.managed)
    if previous_path.is_file():
        previous = _read_json(previous_path.read_text(encoding="utf-8"), "identity dependency snapshot")
        old_managed = previous.get("managed")
        if previous.get("schema") != 1 or not isinstance(old_managed, list) or any(
            not isinstance(name, str) for name in old_managed
        ):
            raise WinappError("Unsupported identity dependency snapshot.")
        managed.update(old_managed)
    editable_sources = {}
    for package in packages:
        origin = package["direct_url"]
        if (package["name"] not in managed and isinstance(origin, dict)
            and isinstance(origin.get("dir_info"), dict) and origin["dir_info"].get("editable") is True):
            if not isinstance(origin.get("url"), str) or urlparse(origin["url"]).scheme != "file":
                raise WinappError(f"Cannot record the editable source for {package['name']}.")
            editable_sources[package["name"]] = origin["url"]
    args = ["freeze", "--all"]
    for name in sorted(managed | editable_sources.keys()):
        args.extend(("--exclude", name))
    requirements = run_tool(pip_command(entry, *args), project, environment=environment)
    # pip freeze can emit unquoted Windows editable paths whose backslashes are lost when read back.
    requirements = requirements.rstrip() + "\n"
    requirements += "".join(f'-e "{url}"\n' for url in editable_sources.values())
    snapshot = {
        "schema": 1, "managed": sorted(managed), "packages": packages,
        "requirements": requirements, "environment": str(venv),
    }
    _write_json(previous_path, snapshot)
    return snapshot


def _source_identity(origin: dict | None) -> dict | None:
    if isinstance(origin, dict) and isinstance(origin.get("vcs_info"), dict):
        # freeze pins the resolved commit, not the original branch or tag spelling.
        return {**origin, "vcs_info": {
            key: value for key, value in origin["vcs_info"].items() if key != "requested_revision"
        }}
    return origin


def _validate_restored(snapshot: dict, installed: dict) -> None:
    actual = {item["name"]: item for item in installed["packages"]}
    managed = set(snapshot["managed"]) | set(installed["managed"])
    for package in snapshot["packages"]:
        if package["name"] in managed:
            continue
        restored = actual.get(package["name"])
        if (restored is None or restored["version"] != package["version"]
            or _source_identity(restored["direct_url"]) != _source_identity(package["direct_url"])):
            raise WinappError(f"Could not restore the recorded package {package['name']}=={package['version']}.")


def synchronize_dependencies(
    project: Path, entry: Path, plan: DependencyPlan, *, verbose: bool = False,
    snapshot: dict | None = None,
) -> None:
    environment = installer_environment(project, entry)
    venv = entry.parent.parent
    inputs: list[str] = []
    temporary: list[Path] = []
    try:
        if snapshot is not None:
            for package in snapshot["packages"]:
                if package["name"] in set(snapshot["managed"]) | set(plan.managed):
                    continue
                origin = package.get("direct_url")
                if isinstance(origin, dict) and isinstance(origin.get("url"), str):
                    url = urlparse(origin["url"])
                    if url.scheme == "file":
                        source = Path(url2pathname(url.path))
                        if source.is_relative_to(Path(snapshot["environment"])):
                            raise WinappError(
                                f"Cannot restore {package['name']}: its source is inside the old identity environment. "
                                "Move that source outside the private environment and reinstall it before retrying."
                            )
            restored = local_path(venv / ".winapp-restore.txt", boundary=venv)
            restored.write_text(snapshot["requirements"], encoding="utf-8")
            temporary.append(restored)
            inputs.extend(("-r", str(restored)))
        if plan.requirements is not None:
            inputs.extend(("-r", str(plan.requirements)))
        if plan.bindings is not None:
            inputs.extend(("--editable", str(plan.bindings)))
        if inputs:
            report = local_path(venv / REPORT, boundary=venv)
            report.unlink(missing_ok=True)
            run_tool(
                pip_command(entry, "install", "--disable-pip-version-check", "--no-input",
                            "--only-binary=dynwinrt", "--report", str(report), *inputs),
                project, verbose=verbose, environment=environment,
            )
            if not report.is_file():
                raise WinappError("pip succeeded without writing its installation report.")
            document = _read_json(report.read_text(encoding="utf-8"), "pip installation report")
            if document.get("version") != "1" or not isinstance(document.get("install"), list):
                raise WinappError("Unsupported pip installation report format.")
        run_tool(pip_command(entry, "check"), project, verbose=verbose, environment=environment)
        installed = capture_dependencies(project, entry, plan)
        if snapshot is not None:
            _validate_restored(snapshot, installed)
    finally:
        for path in temporary:
            path.unlink(missing_ok=True)


def installation_hint(entry: Path) -> str:
    def quoted(value: str) -> str:
        return "'" + value.replace("'", "''") + "'"

    return (
        f"Identity Python: {entry}\n"
        "Install application dependencies into this environment, for example:\n"
        f"  & {quoted(sys.executable)} -I -m pip --python {quoted(str(entry))} install <package>\n"
        "Use the distribution's package name; it may differ from the missing import name."
    )
