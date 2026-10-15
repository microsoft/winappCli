# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

import hashlib
import json
import os
from pathlib import Path
import shutil
import stat
import tempfile
import tomllib
import uuid
from collections.abc import Callable

from packaging.requirements import InvalidRequirement, Requirement
from packaging.utils import canonicalize_name
from packaging.version import InvalidVersion, Version

from .config import BindingsConfig
from .errors import WinappError
from .metadata import load_inventory, select_metadata
from .paths import check_output_tree, local_path, unique_paths
from .tools import run_tool

MARKER = ".winapp-python.json"
IGNORED_OUTPUT_DIRS = frozenset({".dynwinrt-build", "__pycache__", "build", "dist"})


def _hash_file(digest, path: Path) -> None:
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)


def _inputs_digest(
    config: BindingsConfig, roots: tuple[Path, ...], refs: tuple[Path, ...], codegen_version: Version,
) -> str:
    inventory = load_inventory(config.project)
    digest = hashlib.sha256(json.dumps({
        "package": config.package,
        "codegen": str(codegen_version),
        "scope": config.scope,
        "types": config.types,
        "additional_winmds": [
            {"path": str(entry.path) if entry.path else None,
             "namespace": entry.namespace, "classes": entry.classes}
            for entry in config.additional_winmds
        ],
        "packages": [(p.name, p.version) for p in inventory.packages],
        "emit": [str(path) for path in roots],
        "refs": [str(path) for path in refs],
    }, sort_keys=True).encode("utf-8"))
    for path in unique_paths([*roots, *refs]):
        digest.update(str(path).lower().encode("utf-8"))
        _hash_file(digest, path)
        documentation = path.with_suffix(".xml")
        if documentation.is_file():
            local_path(documentation)
            _hash_file(digest, documentation)
    return digest.hexdigest()


def _output_stamp(directory: Path) -> str:
    # Checking size/mtime catches ordinary edits without re-reading thousands of
    # generated files on every restore. --force remains available for a rebuild.
    digest = hashlib.sha256()
    for root, directories, files in os.walk(directory, followlinks=False):
        checked = []
        for name in directories:
            if name in IGNORED_OUTPUT_DIRS or name.endswith(".egg-info"):
                continue
            details = (Path(root) / name).lstat()
            if stat.S_ISLNK(details.st_mode) or getattr(details, "st_file_attributes", 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT:
                raise WinappError(f"A generated directory is a symlink or junction: {Path(root) / name}")
            checked.append(name)
        directories[:] = sorted(checked)
        for name in sorted(files):
            if name == MARKER or name.endswith((".pyc", ".pyo")):
                continue
            path = Path(root) / name
            details = path.lstat()
            if stat.S_ISLNK(details.st_mode) or getattr(details, "st_file_attributes", 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT:
                raise WinappError(f"A generated file is a symlink or junction: {path}")
            digest.update(path.relative_to(directory).as_posix().encode("utf-8"))
            digest.update(f"|{details.st_size}|{details.st_mtime_ns}\n".encode("ascii"))
    return digest.hexdigest()


def validate_output(output: Path, package: str) -> None:
    check_output_tree(output)
    if not output.exists() or not any(output.iterdir()):
        return
    marker = output / MARKER
    if not marker.is_file():
        raise WinappError(
            f"Refusing to replace {output}: it is not a winapp-managed Python package. "
            "Move your files elsewhere or choose a different package name."
        )
    try:
        owner = json.loads(marker.read_text(encoding="utf-8"))
    except json.JSONDecodeError as error:
        raise WinappError(f"Invalid ownership marker in {output}; refusing to replace it.") from error
    if not isinstance(owner, dict) or owner.get("schema") != 1 or owner.get("package") != package:
        raise WinappError(f"Unrecognized ownership marker in {output}; refusing to replace it.")


def validate_generated_package(
    output: Path, config: BindingsConfig, codegen_version: Version, *, check_tree: bool = True,
) -> None:
    if check_tree:
        check_output_tree(output)
    else:
        local_path(output / "__init__.py", boundary=output)
        local_path(output / "pyproject.toml", boundary=output)
    if not (output / "__init__.py").is_file() or not (output / "pyproject.toml").is_file():
        raise WinappError("Codegen did not produce an installable Python package; previous output was preserved.")
    try:
        document = tomllib.loads((output / "pyproject.toml").read_text(encoding="utf-8"))
    except tomllib.TOMLDecodeError as error:
        raise WinappError(f"Codegen produced an invalid pyproject.toml: {error}") from error
    project = document.get("project")
    if not isinstance(project, dict) or project.get("name") != config.package.replace("_", "-"):
        raise WinappError("Codegen produced an unexpected package name; previous output was preserved.")
    dependencies = project.get("dependencies")
    if not isinstance(dependencies, list) or any(not isinstance(item, str) for item in dependencies):
        raise WinappError("Codegen did not declare its Python runtime dependency.")
    try:
        runtime = [
            Requirement(item) for item in dependencies
            if canonicalize_name(Requirement(item).name) == "dynwinrt"
        ]
    except InvalidRequirement as error:
        raise WinappError(f"Codegen produced an invalid runtime dependency: {error}") from error
    if (
        len(runtime) != 1 or runtime[0].url or runtime[0].marker
        or len(runtime[0].specifier) != 1
        or next(iter(runtime[0].specifier)).operator != "=="
        or "*" in next(iter(runtime[0].specifier)).version
        or not runtime[0].specifier.contains(codegen_version, prereleases=True)
    ):
        raise WinappError("The generated dynwinrt runtime pin does not match codegen; previous output was preserved.")


def require_current_package(config: BindingsConfig) -> Path:
    output = local_path(config.output, boundary=config.project)
    marker = local_path(output / MARKER, boundary=config.project)
    if not marker.is_file():
        raise WinappError("Configured Python bindings have not been generated. Run 'winapp-py restore' first.")
    try:
        owner = json.loads(marker.read_text(encoding="utf-8"))
    except json.JSONDecodeError as error:
        raise WinappError("Invalid generated bindings marker; run 'winapp-py generate --force'.") from error
    if (
        not isinstance(owner, dict) or owner.get("schema") != 1 or owner.get("package") != config.package
        or not isinstance(owner.get("codegen_version"), str)
    ):
        raise WinappError("Invalid generated bindings marker; run 'winapp-py generate --force'.")
    try:
        selected = Version(owner["codegen_version"])
    except InvalidVersion as error:
        raise WinappError("Invalid generated codegen version; run 'winapp-py generate --force'.") from error
    if config.codegen_version is None or selected != config.codegen_version:
        raise WinappError("Generated bindings do not match the project's codegen-version. Run 'winapp-py restore'.")
    roots, refs = select_metadata(config)
    if (
        owner.get("inputs_digest") != _inputs_digest(config, roots, refs, selected)
        or owner.get("files_stamp") != _output_stamp(output)
    ):
        raise WinappError("Generated Python bindings are stale or changed. Run 'winapp-py restore' before identity execution.")
    validate_generated_package(output, config, selected, check_tree=False)
    return output


def generate(
    config: BindingsConfig, executable: Path, codegen_version: Version, *, verbose: bool = False, force: bool = False,
    report: Callable[[str], None] | None = None,
) -> Path:
    if config.codegen_version is None or codegen_version != config.codegen_version:
        raise WinappError("Codegen does not match the project's codegen-version; bindings were not changed.")
    emit_winmds, refs = select_metadata(config)
    inputs_digest = _inputs_digest(config, emit_winmds, refs, codegen_version)
    output = local_path(config.output, boundary=config.project)
    marker = output / MARKER
    if marker.is_file() and not force:
        local_path(marker, boundary=config.project)
        try:
            cache = json.loads(marker.read_text(encoding="utf-8"))
        except json.JSONDecodeError as error:
            raise WinappError(f"Invalid ownership marker in {output}; refusing to reuse it.") from error
        if not isinstance(cache, dict) or cache.get("schema") != 1 or cache.get("package") != config.package:
            raise WinappError(f"Unrecognized ownership marker in {output}; refusing to reuse it.")
        if (
            cache.get("codegen_version") == str(codegen_version)
            and cache.get("inputs_digest") == inputs_digest
            and cache.get("files_stamp") == _output_stamp(output)
        ):
            validate_generated_package(output, config, codegen_version, check_tree=False)
            message = f"Bindings current: {config.package}"
            if report is None:
                print(message, flush=True)
            else:
                report(message)
            if verbose:
                print(f"Bindings output: {output}", flush=True)
            return output
    validate_output(output, config.package)
    output.parent.mkdir(parents=True, exist_ok=True)

    with tempfile.TemporaryDirectory(prefix=".generate-", dir=output.parent) as temporary:
        staging = Path(temporary)
        # Codegen derives Python import names from the output leaf, so keep that leaf stable.
        generated = staging / config.package
        all_metadata = unique_paths([*emit_winmds, *refs])
        passes: list[tuple[tuple[Path, ...], tuple[Path, ...], str | None, tuple[str, ...]]] = []
        if emit_winmds:
            passes.append((emit_winmds, refs, None, ()))
        if config.types:
            passes.append(((), all_metadata, None, config.types))
        for entry in config.additional_winmds:
            if entry.classes:
                roots = (entry.path,) if entry.path is not None else ()
                references = tuple(path for path in all_metadata if path not in roots)
                passes.append((roots, references, entry.namespace, entry.classes))
        for index, (roots, references, namespace, classes) in enumerate(passes):
            emit_list = staging / f"inputs-{index}.txt"
            ref_list = staging / f"references-{index}.txt"
            emit_list.write_text("".join(f"{path}\n" for path in roots), encoding="utf-8")
            ref_list.write_text("".join(f"{path}\n" for path in references), encoding="utf-8")
            args = [
                str(executable), "generate", "--lang", "py",
                "--winmd-list", str(emit_list), "--ref-list", str(ref_list),
                "--output", str(generated),
            ]
            if namespace is not None:
                args.extend(("--namespace", namespace))
            if classes:
                args.extend(("--class-name", ",".join(classes)))
            run_tool(args, config.project, verbose=verbose)
        validate_generated_package(generated, config, codegen_version)
        (generated / MARKER).write_text(json.dumps({
            "schema": 1, "package": config.package, "codegen_version": str(codegen_version),
            "scope": config.scope, "types": list(config.types),
            "inputs_digest": inputs_digest, "files_stamp": _output_stamp(generated),
        }, indent=2) + "\n", encoding="utf-8")
        validate_output(output, config.package)
        backup = output.parent / f".{config.package}.backup-{uuid.uuid4().hex}"
        had_output = output.exists()
        if had_output:
            output.rename(backup)
        try:
            generated.rename(output)
        except OSError as error:
            if had_output:
                try:
                    backup.rename(output)
                except OSError as restore_error:
                    raise WinappError(
                        f"Could not publish bindings or restore the old directory. "
                        f"Previous bindings remain at {backup}: {restore_error}"
                    ) from error
            raise
        if had_output:
            shutil.rmtree(backup)
    message = f"Bindings generated: {config.package}"
    if report is None:
        print(message, flush=True)
    else:
        report(message)
    if verbose:
        print(f"Bindings output: {output}", flush=True)
    return output
