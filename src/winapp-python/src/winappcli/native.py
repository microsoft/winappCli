# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

from pathlib import Path
import struct
import sysconfig

from .errors import WinappError

REQUIRED_NATIVE_FILES = (
    "winapp.exe", "libHarfBuzzSharp.dll", "libSkiaSharp.dll",
    "WinApp.DevTools.Native.dll", "WinApp.DevTools.Managed.dll", "winapp-devtools-schema.json",
)


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


def native_payload_platform(directory: Path) -> str:
    missing = [
        name for name in REQUIRED_NATIVE_FILES
        if not (directory / name).is_file() or (directory / name).stat().st_size == 0
    ]
    if missing:
        raise WinappError(
            f"Native CLI files are missing or empty in {directory}: {', '.join(missing)}. "
            "Reinstall microsoft-winappcli, or prepare the complete _native directory for source development "
            "as described in docs/development.md."
        )
    return native_platform(directory / "winapp.exe")


def bundled_cli() -> Path:
    root = Path(__file__).resolve().parent / "_native"
    actual = native_payload_platform(root)
    expected = sysconfig.get_platform().replace("-", "_")
    if actual != expected:
        raise WinappError(f"Bundled CLI targets {actual}, but this Python uses {expected}; install the matching wheel.")
    return root / "winapp.exe"
