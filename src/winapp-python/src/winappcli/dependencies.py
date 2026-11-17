# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

from collections.abc import Callable, Iterator
from contextlib import contextmanager
import os
from pathlib import Path
import re
import sys
import tempfile
import tomllib

from packaging.specifiers import InvalidSpecifier, SpecifierSet
from packaging.version import Version
import tomlkit
from tomlkit.toml_document import TOMLDocument

from .errors import WinappError
from .paths import local_path
from .python_runtime import Interpreter, clean_python_environment, probe_interpreter
from .tools import run_tool

INSTALLER_TIMEOUT = "60"
INCLUDE = re.compile(r"^\s*(?:(-r|-c)\s*(?:=\s*)?|(--requirement|--constraint)(?:\s+|=))(.+?)\s*$")


def read_pyproject(project: Path) -> dict:
    source = local_path(project / "pyproject.toml", boundary=project)
    if not source.is_file():
        return {}
    try:
        return tomllib.loads(source.read_text(encoding="utf-8-sig"))
    except tomllib.TOMLDecodeError as error:
        raise WinappError(f"Invalid {source}: {error}") from error


def publish_project_files(candidates: dict[Path, tuple[bytes, bytes]]) -> None:
    staged: dict[Path, Path] = {}
    published: list[Path] = []
    existed = {source: source.exists() for source in candidates}
    try:
        for source, (before, after) in candidates.items():
            local_path(source, boundary=source.parent)
            if before == after:
                continue
            with tempfile.NamedTemporaryFile(
                prefix=f".{source.name}-", dir=source.parent, delete=False,
            ) as stream:
                staged[source] = Path(stream.name)
                stream.write(after)
        for source in staged:
            before, _ = candidates[source]
            if (source.read_bytes() if source.exists() else b"") != before:
                raise WinappError(f"{source} changed during configuration; not overwriting it.")
        for source, temporary in staged.items():
            os.replace(temporary, source)
            published.append(source)
    except (OSError, WinappError):
        for source in reversed(published):
            before, after = candidates[source]
            if source.read_bytes() != after:
                raise WinappError(f"{source} changed during configuration rollback; its new contents were preserved.")
            if existed[source]:
                source.write_bytes(before)
            else:
                source.unlink()
        raise
    finally:
        for temporary in staged.values():
            temporary.unlink(missing_ok=True)


def edit_pyproject(project: Path, update: Callable[[TOMLDocument], None]) -> None:
    source = local_path(project / "pyproject.toml", boundary=project)
    before = source.read_bytes() if source.exists() else b""
    try:
        document = tomlkit.parse(before.decode("utf-8-sig"))
        update(document)
        text = tomlkit.dumps(document)
        if b"\r\n" in before:
            text = text.replace("\r\n", "\n").replace("\n", "\r\n")
        after = (b"\xef\xbb\xbf" if before.startswith(b"\xef\xbb\xbf") else b"") + text.encode("utf-8")
        tomllib.loads(after.decode("utf-8-sig"))
    except (tomlkit.exceptions.ParseError, tomllib.TOMLDecodeError) as error:
        raise WinappError(f"Cannot update {source}: {error}") from error
    publish_project_files({source: (before, after)})


def _requirement_lines(text: str) -> Iterator[str]:
    logical: list[str] = []
    for line in text.splitlines():
        if line.endswith("\\") and not line.lstrip().startswith("#"):
            logical.append(line[:-1])
            continue
        logical.append(line)
        yield "".join(logical)
        logical.clear()
    if logical:
        yield "".join(logical)


def requirements_files(project: Path, source: Path) -> tuple[Path, ...]:
    visited: set[Path] = set()

    def visit(path: Path) -> None:
        path = local_path(path, boundary=project)
        if path in visited:
            return
        if not path.is_file():
            raise WinappError(f"Requirements file does not exist: {path}")
        visited.add(path)
        for line in _requirement_lines(path.read_text(encoding="utf-8-sig")):
            line = line.split(" #", 1)[0].strip()
            if line.startswith("#"):
                continue
            if re.search(r"(?:^|\s)--(?:require-hashes|hash)(?:[=\s]|$)", line):
                raise WinappError(
                    f"Identity requirements cannot enforce hashes while installing local bindings: {path}. "
                    "Provide a separate requirements export without hashes; the original file was not changed."
                )
            match = INCLUDE.match(line)
            if match:
                name = match[3].strip().strip("\"'")
                if "://" in name:
                    raise WinappError("Requirements includes must be local project files, not remote URLs.")
                visit(path.parent / name)

    visit(source)
    return tuple(sorted(visited, key=str))


def validate_project_python(project: Path, interpreter: Interpreter) -> None:
    settings = read_pyproject(project).get("project", {})
    if not isinstance(settings, dict):
        raise WinappError("[project] in pyproject.toml must be a table.")
    requirement = settings.get("requires-python")
    if requirement is None:
        return
    if not isinstance(requirement, str):
        raise WinappError("[project].requires-python must be a version constraint string.")
    try:
        compatible = SpecifierSet(requirement).contains(interpreter.version, prereleases=True)
    except InvalidSpecifier as error:
        raise WinappError(f"Invalid project requires-python: {requirement}") from error
    if not compatible:
        raise WinappError(f"Project requires Python {requirement}, but the selected environment uses {interpreter.version}.")


@contextmanager
def temporary_codegen_python(project: Path, *, verbose: bool = False) -> Iterator[Path]:
    directory = local_path(project / ".winapp", boundary=project)
    directory.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix=".codegen-", dir=directory) as temporary:
        target = local_path(Path(temporary), boundary=project)
        run_tool(
            [sys.executable, "-I", "-m", "venv", "--without-pip", str(target)],
            project, verbose=verbose, environment=clean_python_environment(),
        )
        executable = local_path(target / "Scripts" / "python.exe", boundary=project)
        probe_interpreter(executable, project, require_venv=True)
        yield executable


def pip_command(executable: Path, *arguments: str) -> list[str]:
    return [sys.executable, "-I", "-m", "pip", "--python", str(executable), *arguments]


def installer_environment(project: Path, executable: Path) -> dict[str, str]:
    environment = clean_python_environment()
    if not any(environment.get(name) for name in ("PIP_TIMEOUT", "PIP_DEFAULT_TIMEOUT")):
        configuration = run_tool(
            pip_command(executable, "config", "list"),
            project, environment=environment,
        )
        keys = {line.partition("=")[0].strip().lower() for line in configuration.splitlines()}
        if not keys.intersection({
            "global.timeout", "global.default-timeout", "install.timeout", "install.default-timeout",
        }):
            environment["PIP_DEFAULT_TIMEOUT"] = INSTALLER_TIMEOUT
    return environment


def install_codegen(project: Path, executable: Path, version: Version | None, *, verbose: bool = False) -> None:
    if verbose:
        requested = f"dynwinrt-codegen=={version}" if version is not None else "the latest dynwinrt-codegen"
        print(f"Installing {requested} in the generator environment: {executable}...", flush=True)
    resolution = ["--force-reinstall"] if version is not None else ["--upgrade", "--force-reinstall", "--pre"]
    requirement = f"dynwinrt-codegen=={version}" if version is not None else "dynwinrt-codegen"
    run_tool(
        pip_command(executable, "install", "--disable-pip-version-check", "--no-input",
                    "--only-binary=dynwinrt-codegen", *resolution, requirement),
        project, verbose=verbose, environment=installer_environment(project, executable),
    )
