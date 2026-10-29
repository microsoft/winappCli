# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

from collections import deque
from dataclasses import dataclass
import os
from pathlib import Path
import stat
import sys

from .errors import WinappError
from .metadata import load_inventory, packages_hash
from .paths import local_path

VALUE_OPTIONS = frozenset({
    "--config-dir", "--setup-sdks", "--exe", "--name", "--publisher",
    "--output-dir", "--caller", "--project-framework", "--on",
})
BOOLEAN_OPTIONS = frozenset({
    "--use-defaults", "--no-prompt", "--config-only", "--sparse",
    "--ignore-config", "--no-config", "--no-gitignore", "--force",
    "--quiet", "-q", "--verbose", "-v", "--json",
})
IGNORED_DIRECTORIES = frozenset({
    "node_modules", "bin", "obj", "debug", "release", "packages", "dist",
    "build", "out", "target", "artifacts", "testresults", "__pycache__",
    "venv", "site-packages",
})
MAX_CANDIDATES = 10
BOOLEAN_ALIASES = {
    "--no-prompt": "--use-defaults", "--no-config": "--ignore-config",
    "-q": "--quiet", "-v": "--verbose",
}


@dataclass(frozen=True)
class InitWorkspace:
    project: Path | None
    config_directory: Path | None
    config_only: bool
    skip_bindings: bool


def resolve_init_workspace(arguments: list[str], directory: Path, *, noninteractive: bool) -> InitWorkspace:
    values: dict[str, str] = {}
    booleans: dict[str, bool] = {}
    positional: list[str] = []
    uncertain = False
    command_seen = False
    index = 0
    while index < len(arguments):
        argument = arguments[index]
        index += 1
        if argument == "--":
            positional.extend(arguments[index:])
            break
        if argument == "init" and not command_seen:
            command_seen = True
            continue
        name, separator, value = argument.partition("=")
        if not separator and argument.startswith("-"):
            name, separator, value = argument.partition(":")
        if name in BOOLEAN_OPTIONS:
            if not separator:
                value = "true"
                if index < len(arguments) and arguments[index].lower() in {"true", "false"}:
                    value = arguments[index]
                    index += 1
            booleans[BOOLEAN_ALIASES.get(name, name)] = value.lower() == "true"
        elif name in VALUE_OPTIONS:
            if not separator:
                if index >= len(arguments):
                    uncertain = True
                    continue
                value = arguments[index]
                index += 1
            values[name] = value
        elif argument.startswith("-"):
            uncertain = True
        else:
            positional.append(argument)
    defaults = booleans.get("--use-defaults", False)
    project = None
    if not uncertain and len(positional) <= 1:
        if positional:
            project = local_path(directory / positional[0])
        elif defaults or noninteractive:
            project = local_path(directory)
    config_directory = (
        local_path(directory / values["--config-dir"])
        if "--config-dir" in values else None
    )
    return InitWorkspace(
        project, config_directory, booleans.get("--config-only", False),
        booleans.get("--sparse", False)
        or booleans.get("--ignore-config", False)
        or values.get("--setup-sdks", "").lower() == "none",
    )


def validate_init_workspace(project: Path, *, config_only: bool) -> None:
    if config_only:
        packages_hash(project)
    else:
        load_inventory(project)


def find_init_workspaces(directory: Path, *, config_only: bool) -> list[Path]:
    directory = local_path(directory)
    pending = deque([directory])
    candidates: list[Path] = []
    while pending and len(candidates) < MAX_CANDIDATES:
        project = pending.popleft()
        if (project / "pyvenv.cfg").exists():
            continue
        try:
            source = local_path(project / "winapp.yaml", boundary=project)
            inventory = local_path(project / ".winapp" / "winmds.lock.json", boundary=project)
            if source.is_file() and (config_only or inventory.is_file()):
                validate_init_workspace(project, config_only=config_only)
                candidates.append(project)
        except WinappError as error:
            print(f"warning: Skipping bindings candidate {project}: {error}", file=sys.stderr)
        try:
            with os.scandir(project) as entries:
                children = []
                for entry in entries:
                    if entry.name.startswith(".") or entry.name.lower() in IGNORED_DIRECTORIES:
                        continue
                    info = entry.stat(follow_symlinks=False)
                    if (stat.S_ISDIR(info.st_mode) and not stat.S_ISLNK(info.st_mode)
                            and not getattr(info, "st_file_attributes", 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT):
                        children.append(Path(entry.path))
                pending.extend(sorted(children, key=lambda path: str(path).lower()))
        except PermissionError as error:
            print(f"warning: Cannot search bindings targets in {project}: {error}", file=sys.stderr)
    if pending:
        print("warning: Bindings target search reached 10 candidates; specify a directory if yours is not listed.",
              file=sys.stderr)
    return candidates
