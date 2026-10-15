# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

from contextlib import contextmanager
import json
import os
from pathlib import Path
import re
import sysconfig
from typing import Iterator


def bootstrap_dll_path(project: Path, python_platform: str) -> Path:
    architecture = {"win-amd64": "x64", "win-arm64": "arm64"}.get(python_platform)
    if architecture is None:
        raise RuntimeError("Use Windows x64 or ARM64 CPython.")
    dll = project / ".winapp" / "bin" / architecture / "Microsoft.WindowsAppRuntime.Bootstrap.dll"
    if not dll.is_file():
        raise FileNotFoundError(f"Bootstrap DLL is missing: {dll}. Run winapp-py restore.")
    return dll


def windows_appsdk_version(project: Path) -> tuple[int, int]:
    inventory = json.loads((project / ".winapp" / "winmds.lock.json").read_text(encoding="utf-8-sig"))
    if not isinstance(inventory, dict) or inventory.get("schema") != 3:
        raise ValueError("Invalid SDK inventory. Run winapp-py restore.")
    packages = inventory.get("packages")
    if not isinstance(packages, list):
        raise ValueError("SDK inventory has no package list. Run winapp-py restore.")
    versions = [
        package.get("version") for package in packages
        if isinstance(package, dict) and str(package.get("name", "")).lower() == "microsoft.windowsappsdk"
    ]
    if len(versions) != 1 or not isinstance(versions[0], str):
        raise ValueError("The restored inventory must contain one Microsoft.WindowsAppSDK package.")
    version = re.fullmatch(r"(\d+)\.(\d+)(?:\.\d+){1,2}", versions[0])
    if version is None:
        raise ValueError("This sample needs stable Windows App SDK packages.")
    return int(version[1]), int(version[2])


@contextmanager
def windows_runtime(project: Path) -> Iterator[None]:
    from dynwinrt import RoApartment, init_winappsdk, projected_lifetime_scope

    dll = bootstrap_dll_path(project, sysconfig.get_platform())
    major, minor = windows_appsdk_version(project)
    os.environ["WINAPPSDK_BOOTSTRAP_DLL_PATH"] = str(dll)
    bootstrap = init_winappsdk(major, minor)
    print(f"Bootstrap: {dll}", flush=True)
    try:
        # WinUI's main thread is an STA; release projections before leaving it.
        with RoApartment(0), projected_lifetime_scope():
            yield
    finally:
        del bootstrap
