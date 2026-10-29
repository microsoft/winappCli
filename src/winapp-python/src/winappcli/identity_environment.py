# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

import hashlib
import json
import os
from pathlib import Path

from .config import try_load_config
from .dependencies import detect_dependencies, requirements_files, synchronize_project, validate_project_python
from .errors import WinappError
from .generate import require_current_package
from .paths import local_path
from .python_runtime import Interpreter, clean_python_environment


def application_environment(venv: Path) -> dict[str, str]:
    environment = clean_python_environment()
    environment["VIRTUAL_ENV"] = str(venv)
    environment["PATH"] = str(venv / "Scripts") + os.pathsep + environment.get("PATH", "")
    return environment


def dependency_inputs(
    project: Path, selected: Path | None, interpreter: Interpreter,
) -> tuple[Path, Path | None, str]:
    dependencies = detect_dependencies(project, selected)
    source = dependencies.source
    if not source.is_file():
        raise WinappError(
            f"Development identity needs {source}. Declare application dependencies in the project, "
            "pass --requirements <file>, or provide an empty file for a standard-library-only script."
        )
    files = list(requirements_files(project, source)) if dependencies.kind == "requirements" else [source]
    if dependencies.kind == "uv":
        lock = local_path(project / "uv.lock", boundary=project)
        if not lock.is_file():
            raise WinappError("Identity execution needs this uv project's lockfile. Run 'winapp restore' first.")
        files.append(lock)
    pyproject = local_path(project / "pyproject.toml", boundary=project)
    if pyproject.is_file():
        validate_project_python(project, interpreter)
        if pyproject not in files:
            files.append(pyproject)
    configuration = local_path(project / "winapp.yaml", boundary=project)
    if configuration.is_file():
        files.append(configuration)
    config = try_load_config(project)
    bindings = require_current_package(config) if config is not None else None
    if bindings is not None:
        files.append(bindings / "pyproject.toml")
        if dependencies.kind == "requirements" and dependencies.development_file.exists():
            files.extend(
                path for path in requirements_files(project, dependencies.development_file)
                if path not in files
            )
    digest = hashlib.sha256()
    digest.update(json.dumps({"requirements": str(source), "bindings": str(bindings)}, sort_keys=True).encode())
    for file in files:
        digest.update(str(file).encode())
        digest.update(file.read_bytes())
    return source, bindings, digest.hexdigest()


def synchronize_dependencies(
    project: Path, entry: Path, environment: Path, requirements: Path, bindings: Path | None,
    *, verbose: bool = False,
) -> None:
    synchronize_project(project, entry, bindings, source=requirements, private=True, verbose=verbose)
