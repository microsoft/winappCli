# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

from dataclasses import dataclass
import keyword
from pathlib import Path
import re
import sys
from typing import Literal

import tomlkit
from packaging.version import InvalidVersion, Version

from .dependencies import edit_pyproject, read_pyproject
from .errors import WinappError
from .paths import local_path, unique_paths, winmd_path
from .python_runtime import validate_catalog_version

CONFIG_TABLE = "tool.winapp.python-bindings"
IDENTITY_CONFIG_TABLE = "tool.winapp.python-identity"
DEFAULT_PACKAGE = "winapp_bindings"
BindingScope = Literal["default", "types"]


@dataclass(frozen=True)
class AdditionalWinmd:
    path: Path | None = None
    namespace: str | None = None
    classes: tuple[str, ...] = ()


@dataclass(frozen=True)
class BindingsConfig:
    project: Path
    package: str
    types: tuple[str, ...] = ()
    additional_winmds: tuple[AdditionalWinmd, ...] = ()
    additional_refs: tuple[Path, ...] = ()
    scope: BindingScope = "default"
    codegen_version: Version | None = None

    @property
    def output(self) -> Path:
        return self.project / ".winapp" / "python" / self.package


def _strings(value: object, key: str) -> list[str]:
    if not isinstance(value, list) or any(
        not isinstance(item, str) or not item.strip() or item != item.strip()
        for item in value
    ):
        raise WinappError(f"[{CONFIG_TABLE}].{key} must be an array of nonempty strings.")
    return value


def _validate_package(package: object) -> str:
    if (
        not isinstance(package, str)
        or not re.fullmatch(r"[a-z][a-z0-9_]{0,79}", package)
        or keyword.iskeyword(package)
        or package in sys.stdlib_module_names
        or package in {"dynwinrt", "winapp_cli"}
    ):
        raise WinappError(
            f"[{CONFIG_TABLE}].package must be a lowercase Python package name "
            "(letters, digits and underscores), not a keyword, standard library module, "
            "dynwinrt or winapp_cli."
        )
    return package


def _validate_types(types: list[str]) -> tuple[str, ...]:
    if any(
        not re.fullmatch(r"[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)+", name, flags=re.ASCII)
        or name.startswith("Windows.Win32.")
        for name in types
    ):
        raise WinappError(
            "types must be fully qualified WinRT classes or interfaces, "
            "for example Windows.Foundation.Uri. Python Win32/COM generation is not supported."
        )
    return tuple(sorted(set(types)))


def _metadata_path(project: Path, value: object, key: str) -> Path:
    if not isinstance(value, str) or not value.strip() or value != value.strip():
        raise WinappError(f"{key} must be a nonempty project-relative WinMD path.")
    path = Path(value)
    if path.is_absolute() or path.drive:
        raise WinappError(f"{key} paths must be project-relative: {value}")
    return winmd_path(project / path, boundary=project)


def _additional_winmds(project: Path, value: object) -> tuple[AdditionalWinmd, ...]:
    if not isinstance(value, list):
        raise WinappError(f"[{CONFIG_TABLE}].additional-winmds must be an array of tables.")
    entries: list[AdditionalWinmd] = []
    for raw in value:
        if not isinstance(raw, dict) or raw.keys() - {"winmd-path", "namespace", "classes"}:
            raise WinappError("Each additional-winmds entry accepts only winmd-path, namespace and classes.")
        path = _metadata_path(project, raw["winmd-path"], "winmd-path") if "winmd-path" in raw else None
        namespace = raw.get("namespace")
        classes = _strings(raw.get("classes", []), "classes")
        if namespace is not None and (
            not isinstance(namespace, str)
            or not re.fullmatch(r"[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*", namespace, flags=re.ASCII)
            or namespace == "Windows.Win32" or namespace.startswith("Windows.Win32.")
        ):
            raise WinappError("additional-winmds.namespace must be a WinRT namespace, not Windows.Win32.")
        if bool(namespace) != bool(classes) or any(
            not re.fullmatch(r"[A-Za-z_]\w*", name, flags=re.ASCII) for name in classes
        ):
            raise WinappError("Set namespace and an array of unqualified classes together in additional-winmds.")
        if path is None and not classes:
            raise WinappError("Each additional-winmds entry needs winmd-path or namespace with classes.")
        entry = AdditionalWinmd(path, namespace, tuple(sorted(set(classes))))
        if entry not in entries:
            entries.append(entry)
    return tuple(entries)


def try_load_config(project: Path) -> BindingsConfig | None:
    project = local_path(project)
    if not project.is_dir():
        raise WinappError(f"Project directory does not exist: {project}")
    document = read_pyproject(project)
    tool = document.get("tool", {})
    if not isinstance(tool, dict):
        raise WinappError("[tool] in pyproject.toml must be a TOML table.")
    winapp = tool.get("winapp", {})
    if not isinstance(winapp, dict):
        raise WinappError("[tool.winapp] in pyproject.toml must be a TOML table.")
    config = winapp.get("python-bindings")
    if config is None:
        return None
    if not isinstance(config, dict):
        raise WinappError(f"[{CONFIG_TABLE}] must be a TOML table.")
    unknown = config.keys() - {"package", "types", "additional-winmds", "additional-refs", "scope", "codegen-version"}
    if unknown:
        raise WinappError(
            f"Unknown [{CONFIG_TABLE}] setting(s): {', '.join(sorted(unknown))}."
        )
    codegen_version = None
    if "codegen-version" in config:
        value = config["codegen-version"]
        if not isinstance(value, str) or not value or value != value.strip():
            raise WinappError(f"[{CONFIG_TABLE}].codegen-version must be an exact Python package version.")
        try:
            codegen_version = Version(value)
        except InvalidVersion as error:
            raise WinappError(f"[{CONFIG_TABLE}].codegen-version must be an exact Python package version: {value}") from error
    package = _validate_package(config.get("package"))
    scope = config.get("scope", "default")
    if scope not in ("types", "default"):
        raise WinappError(f"[{CONFIG_TABLE}].scope must be 'default' or 'types'.")
    types = _validate_types(_strings(config.get("types", []), "types"))
    additional = _additional_winmds(project, config.get("additional-winmds", []))
    if scope == "types" and not types and not additional:
        raise WinappError(f"[{CONFIG_TABLE}].types or additional-winmds must select something to generate.")
    refs = tuple(
        _metadata_path(project, name, "additional-refs")
        for name in _strings(config.get("additional-refs", []), "additional-refs")
    )
    return BindingsConfig(project, package, types, additional, unique_paths(refs), scope, codegen_version)


def load_config(project: Path) -> BindingsConfig:
    config = try_load_config(project)
    if config is None:
        raise WinappError(f"Missing [{CONFIG_TABLE}] in {local_path(project) / 'pyproject.toml'}.")
    return config


def load_identity_catalog_version(project: Path) -> str | None:
    document = read_pyproject(project)
    tool = document.get("tool", {})
    if not isinstance(tool, dict):
        raise WinappError("[tool] in pyproject.toml must be a TOML table.")
    winapp = tool.get("winapp", {})
    if not isinstance(winapp, dict):
        raise WinappError("[tool.winapp] in pyproject.toml must be a TOML table.")
    settings = winapp.get("python-identity", {})
    if not isinstance(settings, dict):
        raise WinappError(f"[{IDENTITY_CONFIG_TABLE}] must be a TOML table.")
    unknown = settings.keys() - {"uv-catalog-version"}
    if unknown:
        raise WinappError(f"Unknown [{IDENTITY_CONFIG_TABLE}] setting(s): {', '.join(sorted(unknown))}.")
    if "uv-catalog-version" not in settings:
        return None
    return validate_catalog_version(settings["uv-catalog-version"])


def record_identity_catalog_version(project: Path, version: str) -> None:
    version = validate_catalog_version(version)

    def update(document) -> None:
        if load_identity_catalog_version(project) is not None:
            raise WinappError("The Python identity catalog configuration changed during selection; retry.")
        settings = document.setdefault("tool", tomlkit.table()).setdefault("winapp", tomlkit.table())
        table = settings.setdefault("python-identity", tomlkit.table())
        table["uv-catalog-version"] = version

    edit_pyproject(project, update)


def set_codegen_version(project: Path, version: Version) -> BindingsConfig:
    load_config(project)

    def update(document) -> None:
        document["tool"]["winapp"]["python-bindings"]["codegen-version"] = str(version)

    edit_pyproject(project, update)
    return load_config(project)


def add_config(project: Path, package: str, *, reset: bool = False) -> BindingsConfig:
    project = local_path(project)
    existing = try_load_config(project)
    if existing is not None and not reset:
        raise WinappError(f"[{CONFIG_TABLE}] already exists; edit pyproject.toml to change it.")
    _validate_package(package)

    def update(document) -> None:
        settings = document.setdefault("tool", tomlkit.table()).setdefault("winapp", tomlkit.table())
        table = tomlkit.table()
        table["package"] = package
        table["scope"] = "default"
        table["types"] = []
        table["additional-winmds"] = []
        table["additional-refs"] = []
        if existing is not None and existing.codegen_version is not None:
            table["codegen-version"] = str(existing.codegen_version)
        settings["python-bindings"] = table

    edit_pyproject(project, update)
    return load_config(project)
