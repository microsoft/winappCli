# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

import hashlib
from http.client import HTTPException
import json
import os
from dataclasses import asdict, dataclass
from pathlib import Path, PurePosixPath, PureWindowsPath
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile
from urllib.error import URLError
from urllib.parse import unquote, urlparse
from urllib.request import Request, urlopen

from packaging.version import InvalidVersion, Version

from .errors import WinappError
from .paths import local_path
from .tools import run_tool

# Catalog from uv 0.13.1. Update the commit and raw-file digest together.
CATALOG_COMMIT = "62faed3351cfee2121b3c28b937b1f4b93233a93"
CATALOG_SHA256 = "929d88b43f30bc657b480cdba1aa67dc413a446721e29c795320e2ecf24ea093"
CATALOG_URL = (
    f"https://raw.githubusercontent.com/astral-sh/uv/{CATALOG_COMMIT}/"
    "crates/uv-python-managed/download-metadata.json"
)
MAX_CATALOG_BYTES = 8 * 1024 * 1024
MAX_ARCHIVE_BYTES = 512 * 1024 * 1024
MAX_EXTRACTED_BYTES = 1024 * 1024 * 1024
PROBE = """
import json, sys, sysconfig
print(json.dumps({
    "version": sys.version.split()[0], "implementation": sys.implementation.name,
    "platform": sysconfig.get_platform(), "os": sys.platform,
    "free_threaded": bool(sysconfig.get_config_var("Py_GIL_DISABLED")),
    "debug": bool(sysconfig.get_config_var("Py_DEBUG")),
    "prefix": sys.prefix, "base_prefix": sys.base_prefix
}))
"""


@dataclass(frozen=True)
class Interpreter:
    version: str
    architecture: str
    free_threaded: bool = False
    debug: bool = False

    @property
    def variant(self) -> str:
        return "+".join(name for name, enabled in (
            ("freethreaded", self.free_threaded), ("debug", self.debug)
        ) if enabled)

    def document(self) -> dict:
        return asdict(self)


@dataclass(frozen=True)
class Distribution:
    interpreter: Interpreter
    build: str
    url: str
    sha256: str

    def document(self) -> dict:
        return asdict(self)


def clean_python_environment() -> dict[str, str]:
    environment = dict(os.environ)
    for name in ("PYTHONHOME", "PYTHONPATH", "__PYVENV_LAUNCHER__"):
        environment.pop(name, None)
    return environment


def select_project_python(
    project: Path, selected: Path | None = None, *, require_exists: bool = True,
) -> Path:
    if selected is not None:
        executable = local_path(project / selected)
    else:
        executable = local_path(project / ".venv" / "Scripts" / "python.exe", boundary=project)
        if not executable.is_file() and sys.prefix != sys.base_prefix:
            current = local_path(Path(sys.executable))
            if current.is_relative_to(project):
                executable = current
    if executable.suffix.lower() != ".exe" or (require_exists and not executable.is_file()):
        raise WinappError(
            f"Project Python interpreter not found: {executable}. "
            "Create the project's .venv with 'uv venv .venv' or 'python -m venv .venv', "
            "or pass --python <path-to-python.exe>; "
            "the tool's own Python is not used as a fallback."
        )
    return executable


def probe_interpreter(
    executable: Path, project: Path, *, require_venv: bool = False,
) -> Interpreter:
    try:
        result = subprocess.run(
            [str(executable), "-I", "-c", PROBE], cwd=project,
            capture_output=True, text=True, encoding="utf-8", errors="replace",
            env=clean_python_environment(), timeout=30, check=False,
        )
    except (OSError, subprocess.TimeoutExpired) as error:
        raise WinappError(f"Could not probe project Python at {executable}: {error}") from error
    if result.returncode:
        raise WinappError(
            f"Project Python could not start (exit {result.returncode}): "
            f"{(result.stderr or result.stdout).strip()}. Recreate that environment and retry."
        )
    try:
        data = json.loads(result.stdout)
    except json.JSONDecodeError as error:
        raise WinappError(f"Project Python returned invalid interpreter information: {result.stdout}") from error
    architectures = {"win-amd64": "x86_64", "win-arm64": "aarch64", "win32": "i686"}
    if (
        not isinstance(data, dict) or data.get("implementation") != "cpython"
        or data.get("os") != "win32" or data.get("platform") not in architectures
        or not isinstance(data.get("version"), str)
        or any(type(data.get(key)) is not bool for key in ("free_threaded", "debug"))
    ):
        raise WinappError("Project Python must be Windows CPython with a known architecture.")
    if require_venv:
        if any(not isinstance(data.get(key), str) or not data[key] for key in ("prefix", "base_prefix")):
            raise WinappError("Project Python returned invalid virtual environment information.")
        if Path(data["prefix"]) == Path(data["base_prefix"]):
            raise WinappError(
                f"Bindings installation requires a virtual environment, but {executable} is not one. "
                "Create a project .venv with 'uv venv .venv' or 'python -m venv .venv', "
                "or pass --python <venv-python.exe>; no global packages were changed."
            )
    try:
        parsed = Version(data["version"])
    except InvalidVersion as error:
        raise WinappError(f"Invalid project Python version: {data['version']}") from error
    if len(parsed.release) != 3 or parsed.local is not None or parsed.dev is not None:
        raise WinappError(f"No exact PBS release can be selected for Python {parsed}.")
    return Interpreter(str(parsed), architectures[data["platform"]], data["free_threaded"], data["debug"])


def _read_catalog() -> dict:
    try:
        request = Request(CATALOG_URL, headers={"User-Agent": "microsoft-winappcli", "Accept": "application/json"})
        with urlopen(request, timeout=60) as response:
            payload = response.read(MAX_CATALOG_BYTES + 1)
    except (URLError, OSError, HTTPException) as error:
        raise WinappError(f"Could not read the PBS download catalog: {error}. Check network access and retry.") from error
    if len(payload) > MAX_CATALOG_BYTES:
        raise WinappError("The PBS download catalog exceeds the supported size.")
    if hashlib.sha256(payload).hexdigest() != CATALOG_SHA256:
        raise WinappError("PBS catalog SHA-256 mismatch; no runtime will be downloaded.")
    try:
        document = json.loads(payload)
    except (UnicodeError, json.JSONDecodeError) as error:
        raise WinappError(f"Invalid PBS download catalog: {error}.") from error
    if not isinstance(document, dict):
        raise WinappError("The PBS download catalog is not an object.")
    return document


def validate_distribution(distribution: Distribution) -> None:
    url = urlparse(distribution.url)
    filename = unquote(url.path.rsplit("/", 1)[-1])
    if (
        url.scheme != "https" or url.netloc != "github.com" or url.query or url.fragment
        or not re.fullmatch(r"\d{8}", distribution.build)
        or not url.path.startswith(
            f"/astral-sh/python-build-standalone/releases/download/{distribution.build}/"
        )
        or not re.match(r"^cpython-" + re.escape(distribution.interpreter.version) + r"[+-]", filename)
        or "install_only" not in filename or not filename.endswith(".tar.gz")
        or not re.fullmatch(r"[a-f0-9]{64}", distribution.sha256)
    ):
        raise WinappError("PBS metadata does not identify a checksum-verified publisher release archive.")


def resolve_distribution(interpreter: Interpreter, catalog: dict | None = None) -> Distribution:
    if catalog is None:
        catalog = _read_catalog()
    version = Version(interpreter.version)
    prerelease = "" if version.pre is None else f"{version.pre[0]}{version.pre[1]}"
    matches = []
    for entry in catalog.values():
        if not isinstance(entry, dict):
            continue
        arch = entry.get("arch")
        if (
            entry.get("name") != "cpython" or entry.get("os") != "windows"
            or entry.get("libc") != "none" or not isinstance(arch, dict)
            or arch.get("family") != interpreter.architecture or arch.get("variant") is not None
            or (entry.get("major"), entry.get("minor"), entry.get("patch")) != version.release
            or entry.get("prerelease", "") != prerelease
            or (entry.get("variant") or "") != interpreter.variant
        ):
            continue
        if any(not isinstance(entry.get(key), str) for key in ("build", "url", "sha256")):
            raise WinappError("The matching PBS catalog entry has incomplete download metadata.")
        distribution = Distribution(interpreter, entry["build"], entry["url"], entry["sha256"])
        validate_distribution(distribution)
        matches.append(distribution)
    if len(matches) != 1:
        raise WinappError(
            f"No unique PBS distribution matches CPython {interpreter.version} "
            f"{interpreter.architecture} {interpreter.variant or 'standard ABI'}. "
            "No other Python version or ABI will be substituted. "
            "Upgrade microsoft-winappcli for a newer catalog, or use a project interpreter "
            "covered by this release."
        )
    return matches[0]


def distribution_from_document(document: object, interpreter: Interpreter) -> Distribution:
    if not isinstance(document, dict) or document.get("interpreter") != interpreter.document():
        raise WinappError("The recorded PBS runtime does not match the selected project interpreter.")
    if any(not isinstance(document.get(key), str) for key in ("build", "url", "sha256")):
        raise WinappError("The recorded PBS download metadata is incomplete.")
    distribution = Distribution(interpreter, document["build"], document["url"], document["sha256"])
    validate_distribution(distribution)
    return distribution


def _download(distribution: Distribution, destination: Path) -> None:
    digest = hashlib.sha256()
    total = 0
    try:
        with urlopen(distribution.url, timeout=60) as response, destination.open("xb") as output:
            while chunk := response.read(1024 * 1024):
                total += len(chunk)
                if total > MAX_ARCHIVE_BYTES:
                    raise WinappError("PBS runtime archive exceeds the supported size.")
                digest.update(chunk)
                output.write(chunk)
    except (URLError, OSError, HTTPException) as error:
        raise WinappError(f"Could not download the selected PBS runtime: {error}. Check network access and retry.") from error
    if digest.hexdigest() != distribution.sha256:
        raise WinappError("PBS archive SHA-256 mismatch; no downloaded Python executable will be run.")


def extract_runtime(archive: Path, destination: Path) -> None:
    total = 0
    try:
        with tarfile.open(archive, "r:gz") as bundle:
            for count, member in enumerate(bundle, 1):
                parts = PurePosixPath(member.name.replace("\\", "/"))
                if (
                    count > 100_000 or member.size < 0 or parts.is_absolute()
                    or ".." in parts.parts or not parts.parts or parts.parts[0] != "python"
                    or any(":" in part or PureWindowsPath(part).is_reserved() for part in parts.parts)
                    or not (member.isfile() or member.isdir())
                ):
                    raise WinappError(f"Unsafe or unsupported member in the PBS archive: {member.name}")
                total += member.size
                if total > MAX_EXTRACTED_BYTES:
                    raise WinappError("Expanded PBS runtime exceeds the supported size.")
                if len(parts.parts) == 1:
                    if not member.isdir():
                        raise WinappError("The PBS archive root must be a directory.")
                    continue
                target = local_path(destination.joinpath(*parts.parts[1:]), boundary=destination)
                if member.isdir():
                    target.mkdir(parents=True, exist_ok=True)
                else:
                    target.parent.mkdir(parents=True, exist_ok=True)
                    stream = bundle.extractfile(member)
                    if stream is None:
                        raise WinappError(f"Could not read PBS archive member: {member.name}")
                    with stream, target.open("xb") as output:
                        shutil.copyfileobj(stream, output)
    except (tarfile.TarError, EOFError) as error:
        raise WinappError(f"Invalid PBS runtime archive: {error}") from error


def prepare_runtime(
    project: Path, root: Path, distribution: Distribution, *, verbose: bool = False,
) -> tuple[Path, Path, Path]:
    environment = root / "venv"
    runtime = environment / "runtime"
    if verbose:
        print(
            f"Preparing project-private CPython {distribution.interpreter.version} "
            f"{distribution.interpreter.architecture} from PBS...", flush=True,
        )
    with tempfile.TemporaryDirectory(prefix=".pbs-", dir=root) as temporary:
        archive = Path(temporary) / "python.tar.gz"
        _download(distribution, archive)
        runtime.mkdir(parents=True)
        extract_runtime(archive, runtime)
    names = ("python.exe", "python_d.exe") if distribution.interpreter.debug else ("python.exe",)
    executable = next((runtime / name for name in names if (runtime / name).is_file()), None)
    if executable is None or not (runtime / "Lib" / "os.py").is_file():
        raise WinappError("The selected PBS archive does not contain a complete supported Windows runtime.")
    if probe_interpreter(executable, project) != distribution.interpreter:
        raise WinappError("Downloaded PBS interpreter does not match the project version, architecture or ABI.")
    subprocess_environment = clean_python_environment()
    run_tool([str(executable), "-I", "-m", "venv", str(environment)], project,
             verbose=verbose, environment=subprocess_environment)
    entry = next((environment / "Scripts" / name for name in names
                  if (environment / "Scripts" / name).is_file()), None)
    if entry is None:
        raise WinappError("PBS could not create the project's identity virtual environment.")
    return environment, runtime, entry
