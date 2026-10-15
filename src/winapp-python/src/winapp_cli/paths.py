# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

import os
from pathlib import Path
import stat

from .errors import WinappError


def local_path(path: Path, *, boundary: Path | None = None) -> Path:
    """Check lexical containment before resolving any links."""
    full = Path(os.path.abspath(path))
    if str(full).startswith(("\\\\", "//")):
        raise WinappError(f"Use a local path, not a network path: {full}")
    if "\n" in str(full) or "\r" in str(full):
        raise WinappError(f"Paths cannot contain line breaks: {full!s}")
    if boundary is not None:
        root = Path(os.path.abspath(boundary))
        if full == root or not full.is_relative_to(root):
            raise WinappError(f"Path must be inside {root}: {full}")

    for candidate in (*reversed(full.parents), full):
        try:
            info = candidate.lstat()
        except FileNotFoundError:
            continue
        attributes = getattr(info, "st_file_attributes", 0)
        if stat.S_ISLNK(info.st_mode) or attributes & stat.FILE_ATTRIBUTE_REPARSE_POINT:
            raise WinappError(f"Symlinks and junctions are not supported here: {candidate}")
    return full


def winmd_path(path: Path, *, boundary: Path) -> Path:
    full = local_path(path, boundary=boundary)
    if full.suffix.lower() != ".winmd" or not full.is_file():
        raise WinappError(f"Metadata file is missing or is not a .winmd file: {full}")
    return full


def unique_paths(paths: list[Path]) -> tuple[Path, ...]:
    by_name = {str(path).lower(): path for path in paths}
    return tuple(by_name[key] for key in sorted(by_name))


def check_output_tree(directory: Path) -> None:
    local_path(directory)
    if not directory.exists():
        return
    if not directory.is_dir():
        raise WinappError(f"Expected a generated package directory: {directory}")
    for root, directories, files in os.walk(directory, followlinks=False):
        for name in directories + files:
            local_path(Path(root) / name, boundary=directory)
