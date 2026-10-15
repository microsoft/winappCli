# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

from importlib.metadata import distribution
from pathlib import Path
import struct
import sysconfig

from .errors import WinappError


def native_platform(executable: Path) -> str:
    try:
        with executable.open("rb") as stream:
            header = stream.read(64)
            if len(header) != 64 or header[:2] != b"MZ":
                raise WinappError(f"Native CLI is not a Windows executable: {executable}")
            offset = struct.unpack_from("<I", header, 60)[0]
            stream.seek(offset)
            signature = stream.read(6)
    except OSError as error:
        raise WinappError(f"Could not read native CLI at {executable}: {error}") from error
    if len(signature) != 6 or signature[:4] != b"PE\0\0":
        raise WinappError(f"Native CLI has an invalid PE header: {executable}")
    machine = struct.unpack_from("<H", signature, 4)[0]
    platforms = {0x8664: "win_amd64", 0xAA64: "win_arm64"}
    if machine not in platforms:
        raise WinappError(f"Native CLI has an unsupported Windows architecture (0x{machine:04x}): {executable}")
    return platforms[machine]


def bundled_cli() -> Path | None:
    root = Path(__file__).resolve().parent / "_native"
    executable = root / "winapp.exe"
    if not root.exists():
        metadata = distribution("winapp-cli").read_text("WHEEL")
        if metadata is None:
            raise WinappError("Python package wheel metadata is missing; reinstall winapp-cli.")
        if any(line in metadata.splitlines() for line in
               ("Tag: py3-none-win_amd64", "Tag: py3-none-win_arm64")):
            raise WinappError("Bundled native CLI is missing; reinstall the winapp-cli wheel.")
        return None
    if not executable.is_file():
        raise WinappError(f"Bundled native CLI is missing at {executable}; reinstall winapp-cli.")
    actual = native_platform(executable)
    expected = sysconfig.get_platform().replace("-", "_")
    if actual != expected:
        raise WinappError(f"Bundled CLI targets {actual}, but this Python uses {expected}; install the matching wheel.")
    return executable
