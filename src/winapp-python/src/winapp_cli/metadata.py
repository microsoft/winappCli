# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

import hashlib
import json
from dataclasses import dataclass
from pathlib import Path

import yaml

from .errors import WinappError
from .paths import local_path, unique_paths, winmd_path
from .config import BindingsConfig

RESTORE_HINT = "Run 'python -m winapp_cli restore' to refresh the SDK metadata."
REFERENCE_ONLY = frozenset({
    "microsoft.windows.sdk.cpp",
    "microsoft.windows.sdk.contracts",
    "microsoft.windowsappsdk.winui",
    "microsoft.windowsappsdk.interactiveexperiences",
})


@dataclass(frozen=True)
class WinmdPackage:
    name: str
    version: str
    winmds: tuple[Path, ...]


@dataclass(frozen=True)
class WinmdInventory:
    packages: tuple[WinmdPackage, ...]

    @property
    def winmds(self) -> tuple[Path, ...]:
        return unique_paths([path for package in self.packages for path in package.winmds])


def packages_hash(project: Path) -> str:
    source = local_path(project / "winapp.yaml", boundary=project)
    if not source.is_file():
        raise WinappError(f"Missing {source}. Add the SDK package versions to restore.")
    try:
        document = yaml.load(source.read_text(encoding="utf-8-sig"), Loader=yaml.BaseLoader)
    except yaml.YAMLError as error:
        raise WinappError(f"Invalid {source}: {error}") from error
    keys = (
        [key for key in document if isinstance(key, str) and key.lower() == "packages"]
        if isinstance(document, dict) else []
    )
    if len(keys) > 1:
        raise WinappError(f"{source} contains more than one packages section.")
    packages = document[keys[0]] if keys else None
    if not isinstance(packages, list) or not packages:
        raise WinappError(f"{source} must contain a nonempty packages list.")
    lines: set[str] = set()
    names: set[str] = set()
    for item in packages:
        fields = (
            {key.lower(): value for key, value in item.items() if isinstance(key, str)}
            if isinstance(item, dict) else {}
        )
        if (not isinstance(item, dict) or len(fields) != len(item)
                or set(fields) != {"name", "version"} or any(
                    not isinstance(fields[key], str) or not fields[key].strip()
                    for key in ("name", "version")
                )):
            raise WinappError(f"Each package in {source} must have only string name and version fields.")
        name = fields["name"].lower()
        if name in names:
            raise WinappError(f"Duplicate package name in {source}: {fields['name']}")
        names.add(name)
        lines.add(f"{name}|{fields['version']}")
    return hashlib.sha256("\n".join(sorted(lines)).encode("utf-8")).hexdigest()


def load_inventory(project: Path) -> WinmdInventory:
    source = local_path(project / ".winapp" / "winmds.lock.json", boundary=project)
    if not source.is_file():
        raise WinappError(f"Missing {source}. {RESTORE_HINT}")
    try:
        inventory = json.loads(source.read_text(encoding="utf-8-sig"))
    except json.JSONDecodeError as error:
        raise WinappError(f"Invalid metadata inventory {source}: {error}. {RESTORE_HINT}") from error
    if not isinstance(inventory, dict) or inventory.get("schema") != 3:
        raise WinappError(f"Unsupported metadata inventory schema in {source}. {RESTORE_HINT}")
    if inventory.get("yaml_packages_hash") != packages_hash(project):
        raise WinappError(f"winapp.yaml has changed or its inventory hash is missing. {RESTORE_HINT}")

    cache_name = inventory.get("nuget_cache_dir")
    if not isinstance(cache_name, str) or not Path(cache_name).is_absolute():
        raise WinappError(f"The inventory has no absolute NuGet cache directory. {RESTORE_HINT}")
    cache = local_path(Path(cache_name))
    if cache == cache.parent or not cache.is_dir():
        raise WinappError(f"The inventory has an invalid NuGet cache directory. {RESTORE_HINT}")
    packages = inventory.get("packages")
    if not isinstance(packages, list):
        raise WinappError(f"The inventory has no packages list. {RESTORE_HINT}")

    entries_by_package: list[WinmdPackage] = []
    paths: list[Path] = []
    has_windows_metadata = False
    for package in packages:
        if not isinstance(package, dict) or any(
            not isinstance(package.get(key), str) or not package[key]
            for key in ("name", "version")
        ):
            raise WinappError(f"Invalid package entry in {source}. {RESTORE_HINT}")
        entries = package.get("winmds")
        if not isinstance(entries, list) or any(
            not isinstance(entry, str) or not Path(entry).is_absolute() for entry in entries
        ):
            raise WinappError(f"Invalid metadata paths in {source}. {RESTORE_HINT}")
        resolved = tuple(winmd_path(Path(entry), boundary=cache) for entry in entries)
        paths.extend(resolved)
        entries_by_package.append(WinmdPackage(package["name"], package["version"], resolved))
        if entries and package["name"].lower() in {
            "microsoft.windows.sdk.cpp", "microsoft.windows.sdk.contracts"
        }:
            has_windows_metadata = True
    if not paths:
        raise WinappError(f"The restored SDKs contain no WinRT metadata. {RESTORE_HINT}")
    if not has_windows_metadata:
        raise WinappError(
            "Add Microsoft.Windows.SDK.Contracts or Microsoft.Windows.SDK.CPP to winapp.yaml "
            "and restore it. Explicit Windows metadata avoids using an unpinned machine SDK."
        )
    return WinmdInventory(tuple(entries_by_package))


def read_inventory(project: Path) -> tuple[Path, ...]:
    return load_inventory(project).winmds


def select_metadata(config: BindingsConfig) -> tuple[tuple[Path, ...], tuple[Path, ...]]:
    inventory = load_inventory(config.project)
    emit: list[Path] = [entry.path for entry in config.additional_winmds
                        if entry.path is not None and not entry.classes]
    refs: list[Path] = [
        *config.additional_refs,
        *(entry.path for entry in config.additional_winmds if entry.path is not None and entry.classes),
    ]
    for package in inventory.packages:
        name = package.name.lower()
        if name == "microsoft.web.webview2":
            continue
        if config.scope == "types" or name in REFERENCE_ONLY:
            refs.extend(package.winmds)
        else:
            emit.extend(package.winmds)
    if not emit and not config.types and not any(entry.classes for entry in config.additional_winmds):
        raise WinappError(
            "No metadata matched the default generation policy. Add SDK packages to winapp.yaml "
            "and restore, or select types/additional-winmds in pyproject.toml."
        )
    roots = unique_paths(emit)
    emitted = {str(path).lower() for path in roots}
    return roots, unique_paths([path for path in refs if str(path).lower() not in emitted])
