# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

from collections.abc import Callable, Iterator, Mapping
from dataclasses import dataclass
import os
from pathlib import Path
import re
import shutil
import sys
import tempfile
import tomllib
from typing import Literal

from packaging.requirements import InvalidRequirement, Requirement
from packaging.specifiers import InvalidSpecifier, SpecifierSet
from packaging.utils import canonicalize_name
from packaging.version import Version
import tomlkit
from tomlkit.toml_document import TOMLDocument

from .errors import WinappError
from .paths import local_path
from .python_runtime import Interpreter, clean_python_environment, probe_interpreter, select_project_python
from .tools import installed_codegen_version, run_tool

INSTALLER_TIMEOUT = "60"
MANAGED_PACKAGES = frozenset({"dynwinrt", "dynwinrt-codegen"})
DependencyKind = Literal["requirements", "pyproject", "uv"]
INCLUDE = re.compile(r"^\s*(?:(-r|-c)\s*(?:=\s*)?|(--requirement|--constraint)(?:\s+|=))(.+?)\s*$")


@dataclass(frozen=True)
class ProjectDependencies:
    project: Path
    kind: DependencyKind
    source: Path

    @property
    def development_file(self) -> Path:
        return local_path(self.project / "requirements-dev.txt", boundary=self.project)


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


def detect_dependencies(project: Path, selected: Path | None = None) -> ProjectDependencies:
    project = local_path(project)
    if selected is not None:
        source = local_path(project / selected, boundary=project)
        if not source.is_file():
            raise WinappError(f"Requirements file does not exist: {source}")
        return ProjectDependencies(project, "requirements", source)
    document = read_pyproject(project)
    tool = document.get("tool", {})
    if not isinstance(tool, dict):
        raise WinappError("[tool] in pyproject.toml must be a TOML table.")
    for manager, filenames in (
        ("Poetry", ("poetry.lock",)), ("PDM", ("pdm.lock",)),
        ("Pipenv", ("Pipfile", "Pipfile.lock")),
    ):
        if manager.lower() in tool or any((project / filename).exists() for filename in filenames):
            raise WinappError(
                f"{manager}-managed dependencies are not supported by automatic Python bindings setup. "
                f"Manage the bindings with {manager}; "
                "winapp will not bypass its lockfile with pip."
            )
    settings = document.get("project", {})
    if not isinstance(settings, dict):
        raise WinappError("[project] in pyproject.toml must be a TOML table.")
    dynamic = settings.get("dynamic", [])
    if not isinstance(dynamic, list) or any(not isinstance(item, str) for item in dynamic):
        raise WinappError("[project].dynamic must be an array of field names.")
    if "dependencies" in dynamic:
        raise WinappError(
            "Dynamic project dependencies cannot be edited or restored by winapp. "
            "Use static [project].dependencies or manage bindings dependencies yourself."
        )
    if "uv" in tool or (project / "uv.lock").exists():
        uv = tool.get("uv", {})
        if not isinstance(uv, dict) or not isinstance(uv.get("sources", {}), dict):
            raise WinappError("[tool.uv] and [tool.uv.sources] must be TOML tables.")
        if not settings.get("name"):
            raise WinappError("A uv project needs [project].name before adding Python bindings.")
        return ProjectDependencies(project, "uv", project / "pyproject.toml")
    requirements = local_path(project / "requirements.txt", boundary=project)
    if "dependencies" in settings or (settings and not requirements.exists()):
        return ProjectDependencies(project, "pyproject", project / "pyproject.toml")
    return ProjectDependencies(project, "requirements", requirements)


def _requirement_lines(text: str) -> Iterator[tuple[str, str]]:
    """Yield logical pip declarations alongside their unchanged source text."""
    logical: list[str] = []
    physical: list[str] = []
    for line in text.splitlines(keepends=True):
        physical.append(line)
        value = line.rstrip("\r\n")
        if value.endswith("\\") and not value.lstrip().startswith("#"):
            logical.append(value[:-1])
            continue
        logical.append(value)
        yield "".join(logical), "".join(physical)
        logical.clear()
        physical.clear()
    if physical:
        yield "".join(logical), "".join(physical)


def requirements_files(project: Path, source: Path, *, constraints: bool = True) -> tuple[Path, ...]:
    visited: set[Path] = set()

    def visit(path: Path) -> None:
        path = local_path(path, boundary=project)
        if path in visited:
            return
        if not path.is_file():
            raise WinappError(f"Requirements file does not exist: {path}")
        visited.add(path)
        for line, _ in _requirement_lines(path.read_text(encoding="utf-8-sig")):
            match = INCLUDE.match(line)
            if match and (constraints or (match[1] or match[2]) in {"-r", "--requirement"}):
                name = match[3].split(" #", 1)[0].strip().strip("\"'")
                if "://" in name:
                    raise WinappError("Requirements includes must be local project files, not remote URLs.")
                visit(path.parent / name)

    visit(source)
    return tuple(sorted(visited, key=str))


def _requirement(line: str) -> Requirement | None:
    text = line.split(" #", 1)[0].strip()
    if not text or text.startswith(("#", "-")):
        return None
    try:
        return Requirement(text)
    except InvalidRequirement as error:
        if "://" in text or text.startswith((".", "\\")) or re.match(r"^[A-Za-z]:", text):
            return None
        raise WinappError(f"Invalid requirement: {text}") from error


def _strings(value: object, location: str) -> list[str]:
    if not isinstance(value, list) or any(not isinstance(item, str) for item in value):
        raise WinappError(f"{location} must be an array of dependency strings.")
    for item in value:
        try:
            Requirement(item)
        except InvalidRequirement as error:
            raise WinappError(f"Invalid dependency in {location}: {item}") from error
    return value


def application_requirements(project: Path) -> list[str]:
    settings = read_pyproject(project).get("project", {})
    if not isinstance(settings, dict):
        raise WinappError("[project] in pyproject.toml must be a TOML table.")
    return _strings(settings.get("dependencies", []), "[project].dependencies")


def development_requirements(project: Path) -> list[str]:
    groups = read_pyproject(project).get("dependency-groups", {})
    if not isinstance(groups, dict):
        raise WinappError("[dependency-groups] must be a TOML table.")
    normalized: dict[str, list] = {}
    for name, items in groups.items():
        key = canonicalize_name(name)
        if key in normalized:
            raise WinappError(f"Duplicate dependency group name: {name}")
        normalized[key] = items

    def resolve(name: str, parents: tuple[str, ...] = ()) -> list[str]:
        if name in parents:
            raise WinappError(f"Cyclic dependency group include: {' -> '.join((*parents, name))}")
        if name not in normalized or not isinstance(normalized[name], list):
            raise WinappError(f"Missing or invalid dependency group: {name}")
        requirements: list[str] = []
        for item in normalized[name]:
            if isinstance(item, str):
                requirements.extend(_strings([item], f"[dependency-groups].{name}"))
            elif isinstance(item, dict) and set(item) == {"include-group"} and isinstance(item["include-group"], str):
                requirements.extend(resolve(canonicalize_name(item["include-group"]), (*parents, name)))
            else:
                raise WinappError(f"Invalid dependency in [dependency-groups].{name}: {item}")
        return requirements

    return resolve("dev") if "dev" in normalized else []


def _set_dependency(table, field: str, name: str, replacement: str) -> None:
    if field not in table:
        table[field] = []
    values = table[field]
    indices = [
        index for index, value in enumerate(values)
        if isinstance(value, str) and canonicalize_name(Requirement(value).name) == name
    ]
    if len(indices) > 1:
        raise WinappError(f"Duplicate {name} dependency in {field}.")
    if indices:
        if values[indices[0]] != replacement:
            values[indices[0]] = replacement
    else:
        values.append(replacement)


def _editable_requirements(project: Path, source: Path) -> None:
    if (project / "requirements.in").exists() or (project / "requirements-dev.in").exists():
        raise WinappError(
            "This project uses requirements input/compiled files. Register bindings in the input file "
            "and regenerate its lock with the owning tool; winapp will not edit the generated output."
        )
    if not source.exists():
        return
    for path in requirements_files(project, source):
        text = path.read_text(encoding="utf-8-sig")
        if re.search(r"(?im)autogenerated|automatically generated|do not edit|--(?:require-hashes|hash)[=\s]", text):
            raise WinappError(f"{path} is generated or hash-locked; automatic dependency registration is not supported.")


def _requirements_candidate(
    before: bytes, additions: list[str], *, old_editable: str | None = None,
) -> bytes:
    newline = "\r\n" if b"\r\n" in before else "\n"
    retained: list[str] = []
    for line, original in _requirement_lines(before.decode("utf-8-sig")):
        requirement = _requirement(line)
        name = canonicalize_name(requirement.name) if requirement else None
        if name in MANAGED_PACKAGES:
            if " #" in line:
                retained.append("#" + line.split(" #", 1)[1].rstrip("\r\n") + newline)
        elif old_editable is None or line.strip() != old_editable:
            retained.append(original)
    text = "".join(retained)
    if text and not text.endswith(("\r", "\n")):
        text += newline
    pending = [value for value in additions if value not in {
        line.strip() for line, _ in _requirement_lines(text)
    }]
    text += "".join(value + newline for value in pending)
    return (b"\xef\xbb\xbf" if before.startswith(b"\xef\xbb\xbf") else b"") + text.encode("utf-8")


def register_dependencies(project: Path, package: str, *, previous_package: str | None = None) -> None:
    dependencies = detect_dependencies(project)
    if dependencies.kind == "requirements":
        application = dependencies.source
        development = dependencies.development_file
        application_files = requirements_files(project, application) if application.exists() else (application,)
        development_additions = [] if development in application_files else [f"-r {application.name}"]
        candidates: dict[Path, tuple[bytes, bytes]] = {}
        for root, additions in (
            (application, [f"-e '.\\.winapp\\python\\{package}'"]),
            (development, development_additions),
        ):
            if root == development and not root.exists():
                continue
            _editable_requirements(project, root)
            paths = requirements_files(project, root) if root.exists() else (root,)
            for target in paths:
                if target in candidates:
                    before, current = candidates[target]
                else:
                    before = current = target.read_bytes() if target.exists() else b""
                old = (
                    f"-e '.\\.winapp\\python\\{previous_package}'"
                    if previous_package and previous_package != package and root == application else None
                )
                after = _requirements_candidate(
                    current, additions if target == root else [], old_editable=old,
                )
                candidates[target] = (before, after)
        publish_project_files(candidates)
        return

    def update(document: TOMLDocument) -> None:
        settings = document["project"]
        application_requirements(project)
        if previous_package and previous_package != package:
            values = settings.get("dependencies", [])
            for index in reversed(range(len(values))):
                if canonicalize_name(Requirement(values[index]).name) == canonicalize_name(previous_package):
                    del values[index]
        values = settings.get("dependencies", [])
        for index in reversed(range(len(values))):
            if canonicalize_name(Requirement(values[index]).name) in MANAGED_PACKAGES:
                del values[index]
        if dependencies.kind == "uv":
            _set_dependency(settings, "dependencies", canonicalize_name(package), canonicalize_name(package))
        development_requirements(project)
        for name, values in document.get("dependency-groups", {}).items():
            if not isinstance(values, list):
                raise WinappError(f"[dependency-groups].{name} must be an array of dependencies.")
            for index in reversed(range(len(values))):
                value = values[index]
                if isinstance(value, str):
                    _strings([value], f"[dependency-groups].{name}")
                    if canonicalize_name(Requirement(value).name) in MANAGED_PACKAGES:
                        del values[index]
        if dependencies.kind == "uv":
            sources = document.setdefault("tool", tomlkit.table()).setdefault(
                "uv", tomlkit.table()
            ).setdefault("sources", tomlkit.table())
            name = canonicalize_name(package)
            matching_sources = [key for key in sources if canonicalize_name(key) == name]
            if len(matching_sources) > 1:
                raise WinappError(f"Duplicate normalized source name for {name} in [tool.uv.sources].")
            source_name = matching_sources[0] if matching_sources else name
            path = f".\\.winapp\\python\\{package}"
            existing_source = sources.get(source_name)
            if existing_source is not None and (
                not isinstance(existing_source, Mapping)
                or not isinstance(existing_source.get("path"), str)
                or local_path(project / existing_source["path"], boundary=project)
                != project / ".winapp" / "python" / package
            ):
                raise WinappError(f"[tool.uv.sources].{name} already has a different source; not overwriting it.")
            if previous_package and previous_package != package:
                previous = canonicalize_name(previous_package)
                previous_source = sources.get(previous)
                if isinstance(previous_source, Mapping) and previous_source.get("path") == f".\\.winapp\\python\\{previous_package}":
                    del sources[previous]
            entry = tomlkit.inline_table()
            entry.update({"path": path, "editable": True})
            sources[source_name] = entry

    edit_pyproject(project, update)


def uv_executable() -> str:
    executable = shutil.which("uv.exe") or shutil.which("uv")
    if not executable:
        raise WinappError("This is a uv-managed project, but uv is not available. Install uv and rerun; pip was not used.")
    return executable


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


def ensure_project_python(project: Path, selected: Path | None = None, *, verbose: bool = False) -> Path:
    executable = select_project_python(project, selected, require_exists=False)
    if not executable.is_file():
        if selected is not None:
            return select_project_python(project, selected)
        target = local_path(project / ".venv", boundary=project)
        if target.exists():
            raise WinappError(f"{target} exists but has no working Python; recreate it before continuing.")
        dependencies = detect_dependencies(project)
        if verbose:
            print(f"Creating project environment: {target}...", flush=True)
        arguments = (
            [uv_executable(), "venv", str(target)]
            if dependencies.kind == "uv"
            else [sys.executable, "-I", "-m", "venv", "--without-pip", str(target)]
        )
        run_tool(arguments, project, verbose=verbose, environment=clean_python_environment())
    interpreter = probe_interpreter(executable, project, require_venv=True)
    validate_project_python(project, interpreter)
    return executable


def _pip_command(executable: Path, *arguments: str) -> list[str]:
    return [sys.executable, "-I", "-m", "pip", "--python", str(executable), *arguments]


def installer_environment(project: Path, executable: Path, *, uv: bool = False) -> dict[str, str]:
    environment = clean_python_environment()
    if uv:
        environment.setdefault("UV_HTTP_TIMEOUT", INSTALLER_TIMEOUT)
    elif not any(environment.get(name) for name in ("PIP_TIMEOUT", "PIP_DEFAULT_TIMEOUT")):
        configuration = run_tool(
            _pip_command(executable, "config", "list"),
            project, environment=environment,
        )
        keys = {line.partition("=")[0].strip().lower() for line in configuration.splitlines()}
        if not keys.intersection({
            "global.timeout", "global.default-timeout", "install.timeout", "install.default-timeout",
        }):
            environment["PIP_DEFAULT_TIMEOUT"] = INSTALLER_TIMEOUT
    return environment


def install_codegen(project: Path, executable: Path, version: Version | None, *, verbose: bool = False) -> None:
    if (
        version is not None
        and (executable.parent / "dynwinrt-codegen.exe").is_file()
        and installed_codegen_version(project, executable) == version
    ):
        return
    dependencies = detect_dependencies(project)
    prefix = (
        [uv_executable(), "pip", "install", "--python", str(executable)]
        if dependencies.kind == "uv"
        else _pip_command(executable, "install", "--disable-pip-version-check", "--no-input")
    )
    if verbose:
        requested = f"dynwinrt-codegen=={version}" if version is not None else "the latest dynwinrt-codegen"
        print(f"Installing {requested} with project Python: {executable}...", flush=True)
    resolution = (
        ["--upgrade", "--reinstall", "--prerelease", "allow"]
        if dependencies.kind == "uv" else ["--upgrade", "--force-reinstall", "--pre"]
    )
    if version is not None:
        resolution = ["--reinstall"] if dependencies.kind == "uv" else ["--force-reinstall"]
    requirement = f"dynwinrt-codegen=={version}" if version is not None else "dynwinrt-codegen"
    run_tool([*prefix, "--only-binary=dynwinrt-codegen", *resolution, requirement], project, verbose=verbose,
             environment=installer_environment(project, executable, uv=dependencies.kind == "uv"))


def synchronize_project(
    project: Path, executable: Path, bindings: Path | None, *, source: Path | None = None,
    private: bool = False, verbose: bool = False,
) -> None:
    dependencies = detect_dependencies(
        project, source if source is not None and source.name != "pyproject.toml" else None,
    )
    environment = installer_environment(project, executable, uv=dependencies.kind == "uv")
    if dependencies.kind == "uv":
        arguments = [uv_executable(), "sync", "--python", str(executable)]
        environment["UV_PROJECT_ENVIRONMENT"] = str(executable.parent.parent)
        environment.pop("VIRTUAL_ENV", None)
        if private:
            if not (project / "uv.lock").is_file():
                raise WinappError("Identity execution needs this uv project's lockfile. Run 'winapp-py restore' first.")
            arguments.extend(("--locked", "--no-default-groups", "--no-install-project"))
        elif development_requirements(project):
            arguments.extend(("--group", "dev"))
        if not private and executable.parent.parent == Path(sys.prefix):
            arguments.append("--inexact")
        if verbose:
            print(f"Synchronizing {'identity' if private else 'project'} dependencies with uv...", flush=True)
        run_tool(arguments, project, verbose=verbose, environment=environment)
    else:
        install_inputs: list[str] = []
        if dependencies.kind == "requirements":
            source_files = [dependencies.source]
            if not private and dependencies.development_file.exists():
                source_files.append(dependencies.development_file)
            for source_file in source_files:
                if not source_file.is_file():
                    raise WinappError(f"Missing application dependency declaration: {source_file}")
                install_inputs.extend(("-r", str(source_file)))
        else:
            install_inputs.extend(application_requirements(project))
            if not private:
                install_inputs.extend(development_requirements(project))
        if bindings is not None:
            install_inputs.extend(("--editable", str(bindings)))
        if install_inputs:
            if verbose:
                print(f"Installing declared dependencies with {'identity' if private else 'project'} Python: {executable}...", flush=True)
            run_tool(
                _pip_command(executable, "install", "--disable-pip-version-check", "--no-input",
                             "--only-binary=dynwinrt", *install_inputs),
                project, verbose=verbose, environment=environment,
            )
    run_tool(
        _pip_command(executable, "check"),
        project, verbose=verbose, environment=environment,
    )
