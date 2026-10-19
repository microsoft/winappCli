# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

import os
import json
from pathlib import Path
import shutil
import subprocess
import zipfile

from packaging.version import InvalidVersion, Version

from .errors import WinappError
from .native import bundled_cli

def _child_environment(environment: dict[str, str] | None = None) -> dict[str, str]:
    result = dict(os.environ if environment is None else environment)
    result["WINAPP_CLI_CALLER"] = "python"
    return result


def run_tool(
    arguments: list[str], project: Path, *, verbose: bool = False,
    environment: dict[str, str] | None = None,
) -> str:
    if verbose:
        print(f"> {arguments[0]}", flush=True)
    try:
        result = subprocess.run(
            arguments, cwd=project, text=True, encoding="utf-8", errors="replace",
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, check=False,
            env=_child_environment(environment),
        )
    except OSError as error:
        raise WinappError(f"Could not start {arguments[0]}: {error}") from error
    if result.returncode != 0:
        raise WinappError(
            f"{Path(arguments[0]).name} failed (exit {result.returncode}).\n{result.stdout.strip()}"
        )
    if verbose and result.stdout.strip():
        print(result.stdout.rstrip(), flush=True)
    return result.stdout


def installed_codegen_version(project: Path, python: Path) -> Version | None:
    output = run_tool([
        str(python), "-I", "-c",
        "import importlib.metadata as m, json\n"
        "try: value = m.version('dynwinrt-codegen')\n"
        "except m.PackageNotFoundError: value = None\n"
        "print(json.dumps(value))",
    ], project)
    try:
        value = json.loads(output)
    except json.JSONDecodeError as error:
        raise WinappError("Project Python returned invalid codegen package metadata.") from error
    if value is None:
        return None
    if not isinstance(value, str):
        raise WinappError("Project Python returned invalid codegen package metadata.")
    try:
        return Version(value)
    except InvalidVersion as error:
        raise WinappError("dynwinrt-codegen has invalid version metadata; reinstall the package.") from error


def find_codegen(project: Path, expected_version: Version, python: Path) -> Path:
    executable = python.parent / "dynwinrt-codegen.exe"
    install = subprocess.list2cmdline([
        "winapp", "restore", "--project", str(project), "--python", str(python),
    ])
    required = f"dynwinrt-codegen=={expected_version}"
    if not executable.is_file():
        raise WinappError(
            f"{required} is not installed in the project environment. Run: {install}"
        )
    actual = installed_codegen_version(project, python)
    if actual is None:
        raise WinappError(f"Missing {required} package metadata. Run: {install}")
    if actual != expected_version:
        raise WinappError(
            f"Codegen resolved {required}, but the project environment has {actual}. "
            f"Run: {install}"
        )
    capabilities = set(run_tool([str(executable), "capabilities"], project).splitlines())
    missing = {"lang.py", "selector.namespace-class", "input.winmd-list", "input.ref-list"} - capabilities
    if missing:
        raise WinappError(f"dynwinrt-codegen is missing required capabilities: {', '.join(sorted(missing))}")
    return executable


def find_native_cli(override: str | None) -> Path:
    name = override or os.environ.get("WINAPP_CLI_PATH")
    if not name:
        name = bundled_cli() or shutil.which("winapp.exe")
    if not name:
        raise WinappError(
            "Native winapp.exe was not found in this source installation. "
            "Use --winapp <path>, set WINAPP_CLI_PATH, or install a platform wheel containing the native CLI."
        )
    executable = Path(name).expanduser().absolute()
    if not executable.is_file() or executable.suffix.lower() != ".exe":
        raise WinappError(f"--winapp must point to a native winapp.exe file: {executable}")
    if zipfile.is_zipfile(executable):
        with zipfile.ZipFile(executable) as launcher:
            if "__main__.py" in launcher.namelist():
                raise WinappError(
                    f"Refusing to use a Python wrapper as the native CLI: {executable}. "
                    "Use --winapp <native-exe> or set WINAPP_CLI_PATH to the native build."
                )
    return executable


def forward_native(executable: Path, args: list[str], project: Path) -> int:
    environment = _child_environment()
    try:
        return subprocess.run(
            [str(executable), *args], cwd=project, check=False,
            env=environment,
        ).returncode
    except OSError as error:
        raise WinappError(f"Could not start native winapp at {executable}: {error}") from error
