# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

import argparse
from contextlib import nullcontext, redirect_stdout
import io
from importlib.metadata import version
import os
from pathlib import Path
import platform
import subprocess
import sys

from packaging.version import Version

from .config import (
    DEFAULT_PACKAGE,
    add_config,
    load_config,
    set_codegen_version,
    try_load_config,
)
from .dependencies import (
    ensure_project_python, install_codegen, register_dependencies, synchronize_project,
)
from .errors import WinappError
from .generate import generate
from .identity import clear_identity, run_with_identity
from .init_workspace import find_init_workspaces, resolve_init_workspace, validate_init_workspace
from .metadata import load_inventory, packages_hash
from .paths import local_path
from .progress import Workflow
from .tools import (
    find_codegen,
    find_native_cli,
    forward_native,
    installed_codegen_version,
    run_tool,
)

PYTHON_COMMANDS = frozenset({"init", "restore", "generate", "update", "run", "identity"})
HELP_FLAGS = frozenset({"--help", "-h", "-?", "/?"})
OUTPUT_OPTIONS = frozenset({"--verbose", "-v", "--quiet", "-q", "--json"})
RUN_VALUE_OPTIONS = frozenset({"--project", "--manifest", "--winapp", "--python", "--requirements"})
NATIVE_CLI_HELP = "Native winapp.exe path (otherwise WINAPP_CLI_PATH, bundled CLI, then PATH for source installs)."


def _show_forwarded_help(
    parser: argparse.ArgumentParser, command: str | None, override: str | None,
) -> int:
    if command in {"restore", "update"}:
        parser.print_help()
        return 0
    directory = Path.cwd()
    native = find_native_cli(override)
    if command == "run":
        print("Native run modes (non-Python inputs):", flush=True)
    result = forward_native(native, [command, "--help"] if command else ["--help"], directory)
    if result:
        return result
    print(f"\nPython {command} workflow:" if command else "\nPython extensions:", flush=True)
    if command == "run":
        print(
            "Python .py scripts use the project .venv; --identity uses a private development environment. "
            "Other inputs use the native syntax above.",
            flush=True,
        )
    parser.print_help()
    return 0


class _NativeHelpAction(argparse.Action):
    def __init__(self, option_strings, dest, *, command=None, override=None, **kwargs):
        super().__init__(option_strings, dest, nargs=0, **kwargs)
        self.command = command
        self.override = override

    def __call__(self, parser, namespace, values, option_string=None):
        parser.exit(_show_forwarded_help(
            parser, self.command, getattr(namespace, "winapp", None) or self.override,
        ))


class _VersionAction(argparse.Action):
    def __init__(self, option_strings, dest, *, override=None, **kwargs):
        super().__init__(option_strings, dest, nargs=0, **kwargs)
        self.override = override

    def __call__(self, parser, namespace, values, option_string=None):
        print(f"Python Package: winapp-cli v{version('winapp-cli')}", flush=True)
        native = find_native_cli(self.override)
        print("Native CLI:", flush=True)
        parser.exit(forward_native(native, ["--version"], Path.cwd()))


def _boolean(value: str) -> bool:
    if value.lower() not in {"true", "false"}:
        raise argparse.ArgumentTypeError("expected true or false")
    return value.lower() == "true"


def _add_output_options(parser: argparse.ArgumentParser) -> None:
    for names, help_text in (
        (("--verbose", "-v"), "Show native CLI and codegen output."),
        (("--quiet", "-q"), "Suppress wrapper status without changing application output."),
        (("--json",), argparse.SUPPRESS),
    ):
        parser.add_argument(*names, type=_boolean, nargs="?", const=True,
                            default=argparse.SUPPRESS, help=help_text)


def build_parser(override: str | None = None) -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="winapp-py",
        add_help=False,
        description=(
            "Manage Python WinRT bindings and opt-in development identity. "
            "Other commands are forwarded to the native winapp CLI."
        ),
    )
    parser.add_argument("-h", "--help", action=_NativeHelpAction, override=override,
                        help="Show native CLI help followed by Python extensions.")
    parser.add_argument("--version", action=_VersionAction, override=override,
                        help="Show Python package and native CLI versions.")
    _add_output_options(parser)
    parser.set_defaults(verbose=False, quiet=False, json=False)
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser(
        "init", add_help=False,
        help="Forward native init, then optionally configure, generate and install Python bindings.",
    )
    for command, help_text in (
        ("restore", "Restore pinned SDKs and the project's codegen version, then generate Python bindings."),
        ("generate", "Regenerate bindings with the project's codegen version and existing SDK metadata."),
        ("update", "Update SDK and codegen versions, restore metadata, and regenerate bindings."),
    ):
        child = commands.add_parser(command, add_help=command == "generate",
                                    help=help_text, description=help_text)
        if command != "generate":
            child.add_argument("-h", "--help", action=_NativeHelpAction, command=command,
                               override=override, help="Show native help followed by the Python workflow.")
        child.add_argument("--project", type=Path, default=Path.cwd(),
                           help="Project directory (default: current directory).")
        child.add_argument("--python", type=Path,
                           help="Project venv Python for generation and dependency installation (default: project .venv).")
        _add_output_options(child)
        if command != "generate":
            child.add_argument("--winapp", help=NATIVE_CLI_HELP)
        if command == "update":
            child.add_argument("--channel", choices=("stable", "preview", "experimental"),
                               default="stable", help="SDK version channel (default: stable).")
        if command == "generate":
            child.add_argument("--force", action="store_true", help="Regenerate even when inputs are unchanged.")

    identity = commands.add_parser("identity", help="Manage this project's development identity.")
    _add_output_options(identity)
    identity_commands = identity.add_subparsers(dest="identity_command", required=True)
    clear = identity_commands.add_parser("clear", help="Unregister only the project-owned identity, preserving app data.")
    clear.add_argument("--project", type=Path, default=Path.cwd())
    _add_output_options(clear)

    run = commands.add_parser("run", add_help=False,
                              help="Run a Python script in the project .venv, optionally with development identity.")
    run.add_argument("-h", "--help", action=_NativeHelpAction, command="run", override=override,
                     help="Show native run help followed by Python run options.")
    run.add_argument("--identity", action="store_true",
                     help="Use an exact-match private PBS identity environment; rebuild it when project inputs change.")
    run.add_argument("--project", type=Path, default=Path.cwd())
    run.add_argument("--manifest", type=Path,
                     help="Identity only: project manifest (default: auto-detect in project).")
    run.add_argument("--winapp", help=NATIVE_CLI_HELP)
    _add_output_options(run)
    run.add_argument("--python", type=Path,
                     help="Identity only: project Python to match exactly (default: project .venv).")
    run.add_argument("--requirements", type=Path,
                     help="Identity only: requirements file (default: existing application declarations).")
    run.add_argument("script", type=Path, nargs="?", help="Project-local .py script to execute.")
    run.add_argument("script_args", nargs=argparse.REMAINDER, help="Arguments after '--' go to the script.")
    return parser


def _command_index(argv: list[str]) -> int:
    index = 0
    while index < len(argv) and argv[index].partition("=")[0] in OUTPUT_OPTIONS:
        attached = "=" in argv[index]
        index += 1
        if not attached and index < len(argv) and argv[index].lower() in {"true", "false"}:
            index += 1
    return index


def _native_prefix(argv: list[str]) -> tuple[str | None, list[str]]:
    index = _command_index(argv)
    if len(argv) >= index + 2 and argv[index] == "--winapp":
        return argv[index + 1], argv[:index] + argv[index + 2:]
    if index < len(argv) and argv[index].startswith("--winapp="):
        return argv[index].partition("=")[2], argv[:index] + argv[index + 1:]
    return None, argv


def _normalize_output_options(argv: list[str], command_index: int) -> list[str]:
    command = argv[command_index]
    arguments = argv[:command_index] + argv[command_index + 1:]
    normalized = ["--help" if command == "help" else command]
    index = 0
    while index < len(arguments):
        argument = arguments[index]
        if argument == "--" or (command == "run" and not argument.startswith("-")):
            normalized.extend(arguments[index:])
            break
        name, separator, value = argument.partition("=")
        if command == "run" and name in RUN_VALUE_OPTIONS and not separator:
            normalized.extend(arguments[index:index + 2])
            index += 2
            continue
        index += 1
        if name in OUTPUT_OPTIONS:
            if not separator:
                value = "true"
                if index < len(arguments) and arguments[index].lower() in {"true", "false"}:
                    value = arguments[index]
                    index += 1
            argument = f"{name}={value}"
        normalized.append(argument)
    return normalized


def _run_in_project_venv(
    project: Path, script: Path, script_args: list[str], *, quiet: bool = False,
) -> int:
    if not project.is_dir():
        raise WinappError(f"Project directory does not exist: {project}")
    script = local_path(project / script, boundary=project)
    if not script.is_file():
        raise WinappError(f"An existing project-local .py script is required: {script}")
    python = local_path(project / ".venv" / "Scripts" / "python.exe", boundary=project)
    if not python.is_file():
        raise WinappError(
            f"Project .venv Python was not found: {python}. "
            "Run the script with your own Python interpreter, or use 'uv run <script>' for a uv project."
        )
    if not quiet:
        print(f"Using project .venv: {python}", file=sys.stderr, flush=True)
    try:
        return subprocess.run([str(python), str(script), *script_args], cwd=project, check=False).returncode
    except OSError as error:
        raise WinappError(f"Could not launch the script with project .venv Python: {error}") from error


def _prepare_codegen(
    project: Path, codegen_version: Version | None, *, python_option: Path | None = None, verbose: bool = False,
    progress: Workflow | None = None,
) -> tuple[Path, Path, Version]:
    with progress.step("Prepare environment", "Project environment ready") if progress else nullcontext() as step:
        python = ensure_project_python(project, python_option, verbose=verbose)
        if step is not None:
            environment = python.parent.parent
            location = str(environment.relative_to(project)) if environment.is_relative_to(project) else environment.name
            step.complete(f"Environment: {location}")
        if verbose:
            print(f"Project Python: {python}", flush=True)
    with progress.step("Prepare codegen") if progress else nullcontext() as step:
        install_codegen(project, python, codegen_version, verbose=verbose)
        selected = installed_codegen_version(project, python)
        if selected is None:
            raise WinappError("The installer did not provide dynwinrt-codegen package metadata.")
        if codegen_version is not None and selected != codegen_version:
            raise WinappError(f"The project requires dynwinrt-codegen=={codegen_version}, but the environment has {selected}.")
        codegen = find_codegen(project, selected, python)
        if codegen_version is None:
            set_codegen_version(project, selected)
        if step is not None:
            step.complete(f"Codegen: {selected}")
    return codegen, python, selected


def _bindings_after_sdk(
    project: Path, *, verbose: bool, force: bool = False, python_option: Path | None = None,
    progress: Workflow | None = None, upgrade_codegen: bool = False, configure_dependencies: bool = False,
) -> None:
    config = try_load_config(project)
    if config is None:
        print("SDK packages restored; this project has no Python bindings configuration.", flush=True)
        return
    if config.codegen_version is None and not upgrade_codegen:
        raise WinappError(
            "The project has no codegen-version recorded. "
            "Run 'winapp-py init . --use-defaults --add-python-bindings' or 'winapp-py update' to record it."
        )
    with nullcontext(progress) if progress is not None else Workflow("Preparing Python bindings", verbose=verbose) as workflow:
        codegen, python, selected = _prepare_codegen(
            project, None if upgrade_codegen else config.codegen_version,
            python_option=python_option, verbose=verbose, progress=workflow,
        )
        if upgrade_codegen:
            config = load_config(project)
        with workflow.step("Generate bindings") as step:
            output = generate(config, codegen, selected, verbose=verbose, force=force, report=step.complete)
        with workflow.step("Install dependencies", "Project dependencies installed"):
            if configure_dependencies:
                register_dependencies(project, config.package)
            synchronize_project(project, python, output, verbose=verbose)
        if progress is None:
            workflow.succeed("Python bindings ready.")


def _require_supported_python() -> None:
    if sys.platform != "win32" or platform.python_implementation() != "CPython":
        raise WinappError("Python bindings require CPython on Windows x64 or ARM64.")
    if platform.machine().lower() not in {"amd64", "x86_64", "arm64", "aarch64"} or sys.maxsize <= 2**32:
        raise WinappError("Python bindings require a 64-bit x64 or ARM64 Python interpreter.")


def _configure_after_init(project: Path, *, reset: bool, config_only: bool, verbose: bool) -> None:
    _require_supported_python()
    existing = try_load_config(project)
    package = existing.package if existing is not None else DEFAULT_PACKAGE
    with Workflow("Setting up Python bindings", verbose=verbose) as workflow:
        with workflow.step("Configure project", "Project configured"):
            if existing is None or reset:
                add_config(project, package, reset=reset)
        if config_only:
            _prepare_codegen(project, load_config(project).codegen_version, verbose=verbose, progress=workflow)
            with workflow.step("Register bindings dependency", "Bindings dependency registered"):
                register_dependencies(project, package)
            workflow.succeed("Bindings configured; run 'winapp-py restore' to generate and install them.")
            return
        load_inventory(project)
        _bindings_after_sdk(
            project, verbose=verbose, progress=workflow,
            upgrade_codegen=load_config(project).codegen_version is None,
            configure_dependencies=True,
        )
        workflow.succeed("Python bindings ready.")


def _confirmation(title: str) -> bool:
    while True:
        try:
            answer = input(f"{title} [y/n] (y): ").strip().lower()
        except EOFError as error:
            raise WinappError("Input ended during Python bindings confirmation; no bindings settings were changed.") from error
        if answer in {"", "y", "yes", "true"}:
            return True
        if answer in {"n", "no", "false"}:
            return False


def _native_boolean(arguments: list[str], *names: str) -> bool:
    enabled = False
    for index, argument in enumerate(arguments):
        if argument == "--":
            break
        name, separator, value = argument.partition("=")
        if not separator and argument.startswith("-"):
            name, separator, value = argument.partition(":")
        if name not in names:
            continue
        if not separator:
            value = arguments[index + 1] if index + 1 < len(arguments) else ""
        enabled = value.lower() != "false"
    return enabled


def _bindings_decision(project: Path, arguments: list[str], add: bool) -> tuple[bool, bool]:
    existing = try_load_config(project) is not None
    defaults = _native_boolean(arguments, "--use-defaults", "--no-prompt")
    noninteractive = not sys.stdin.isatty() or bool(os.environ.get("CI"))
    if existing:
        if add:
            return True, False
        if defaults:
            return True, False
        if noninteractive:
            return True, False
        return True, _confirmation("Python bindings are already configured. Reset metadata selections to defaults?")
    if add:
        return True, False
    if defaults or noninteractive:
        return False, False
    return _confirmation("Add Python bindings to call Windows App SDK APIs directly from Python?"), False


def _init_arguments(arguments: list[str]) -> tuple[list[str], bool]:
    boundary = arguments.index("--") if "--" in arguments else len(arguments)
    options = arguments[:boundary]
    add = "--add-python-bindings" in options
    forwarded = [argument for argument in options if argument != "--add-python-bindings"] + arguments[boundary:]
    if add and not any(argument in HELP_FLAGS | {"--cli-schema", "--version"} for argument in options):
        if _native_boolean(options, "--sparse"):
            raise WinappError("--add-python-bindings is incompatible with --sparse; bindings need SDK metadata.")
        for index, argument in enumerate(options):
            value = (
                options[index + 1] if argument == "--setup-sdks" and index + 1 < len(options)
                else argument.partition("=")[2] if argument.startswith("--setup-sdks=") else None
            )
            if value is not None and value.strip().lower() == "none":
                raise WinappError("--add-python-bindings is incompatible with --setup-sdks none.")
        if (
            sys.stdin.isatty() and not os.environ.get("CI")
            and not _native_boolean(options, "--use-defaults", "--no-prompt")
        ):
            raise WinappError(
                "--add-python-bindings requires non-interactive setup. "
                "Add --use-defaults, or omit --add-python-bindings to choose interactively."
            )
    return forwarded, add


def _initialize(native_args: list[str], override: str | None) -> int:
    native_args, add = _init_arguments(native_args)
    working_directory = Path.cwd()
    native = find_native_cli(override)
    result = forward_native(native, native_args, working_directory)
    if result:
        return result
    if any(argument in HELP_FLAGS | {"--cli-schema", "--version"} for argument in native_args):
        if any(argument in HELP_FLAGS for argument in native_args):
            print(
                "\nPython wrapper option:\n"
                "  --add-python-bindings  Enable bindings and project dependency setup during non-interactive init.\n"
                "  Without a directory, interactive init asks you to confirm the bindings target after SDK setup.",
                flush=True,
            )
        return 0
    quiet = _native_boolean(native_args, "--quiet", "-q")
    noninteractive = not sys.stdin.isatty() or bool(os.environ.get("CI"))
    workspace = resolve_init_workspace(native_args, working_directory, noninteractive=noninteractive)
    if workspace.skip_bindings:
        if add:
            raise WinappError("--add-python-bindings needs configured SDK metadata; this init mode skips bindings.")
        if not quiet:
            print("Python bindings were not generated: this init mode skips bindings.", flush=True)
        return 0
    project = workspace.project
    confirmed = False
    if project is None:
        if noninteractive or quiet:
            raise WinappError("The bindings target cannot be confirmed. Specify a directory or use --use-defaults.")
        candidates = find_init_workspaces(working_directory, config_only=workspace.config_only)
        if not candidates:
            raise WinappError(
                "Native init completed, but no usable project-local SDK configuration was found. "
                "Run 'winapp-py init <project-directory> --use-defaults --add-python-bindings'."
            )
        print("SDK artifacts identify possible bindings targets, not the project selected by native.", flush=True)
        if len(candidates) == 1:
            project = candidates[0]
            confirmed = _confirmation(f"Configure Python bindings in {project}?")
        else:
            for index, candidate in enumerate(candidates, 1):
                print(f"  {index}. {candidate.relative_to(working_directory) or '.'}", flush=True)
            print("  0. Leave Python bindings unchanged", flush=True)
            while True:
                try:
                    answer = input("Select the Python bindings target (0 to skip): ").strip()
                except EOFError as error:
                    raise WinappError("Input ended during bindings target selection; no Python files were changed.") from error
                if answer.isdecimal() and 0 <= int(answer) <= len(candidates):
                    selected = int(answer)
                    if selected == 0:
                        return 0
                    project = candidates[selected - 1]
                    confirmed = _confirmation(f"Configure Python bindings in {project}?")
                    break
                print("Enter one of the listed numbers.", flush=True)
        if not confirmed:
            return 0
    if workspace.config_directory is not None and workspace.config_directory != project:
        if add:
            raise WinappError("--add-python-bindings needs project-local winapp.yaml and .winapp metadata.")
        if not quiet:
            print(
                "Python bindings were not generated: native init did not prepare "
                "project-local winapp.yaml and .winapp metadata.", flush=True,
            )
        return 0
    enabled, reset = _bindings_decision(project, native_args, add or confirmed)
    if not enabled:
        return 0
    validate_init_workspace(project, config_only=workspace.config_only)
    with open(os.devnull, "w", encoding="utf-8") if quiet else nullcontext() as output:
        with redirect_stdout(output) if quiet else nullcontext():
            _configure_after_init(
                project, reset=reset, config_only=workspace.config_only,
                verbose=_native_boolean(native_args, "--verbose", "-v"),
            )
    return 0


def main(argv: list[str] | None = None) -> int:
    for stream in (sys.stdout, sys.stderr):
        if isinstance(stream, io.TextIOWrapper):
            stream.reconfigure(encoding="utf-8", errors="replace")
    args_list = list(sys.argv[1:] if argv is None else argv)
    override, parsed = _native_prefix(args_list)
    try:
        if not parsed:
            return _show_forwarded_help(build_parser(override), None, override) or 2
        command_index = _command_index(parsed)
        command = parsed[command_index] if command_index < len(parsed) else None
        if command == "init":
            return _initialize(parsed, override)
        if (
            command not in PYTHON_COMMANDS
            and command not in {"--help", "-h", "--version", "help"}
        ):
            native = find_native_cli(override)
            return forward_native(native, parsed, Path.cwd())
        parser = build_parser(override)
        python_args = _normalize_output_options(parsed, command_index)
        if command == "run":
            args, unknown = parser.parse_known_args(python_args)
            if unknown or (args.script is None and not args.identity) or (
                args.script is not None and (
                    args.script.suffix.lower() != ".py" or (args.project / args.script).is_dir()
                )
            ):
                native = find_native_cli(override)
                return forward_native(native, parsed, Path.cwd())
        else:
            args = parser.parse_args(python_args)
        if args.json:
            parser.error("--json is not supported for Python workflows; use it only with native commands or after the script.")
        if args.quiet and args.verbose:
            parser.error("Cannot specify both --quiet and --verbose options together.")
        _require_supported_python()

        if args.command == "run":
            if args.script is None:
                parser.error("the following arguments are required: script")
            script_args = args.script_args[1:] if args.script_args[:1] == ["--"] else args.script_args
            if not args.identity:
                if args.python is not None or args.requirements is not None or args.manifest is not None or args.winapp:
                    raise WinappError(
                        "--python, --requirements, --manifest and --winapp require --identity for Python scripts. "
                        "Ordinary Python runs use the project .venv."
                    )
                return _run_in_project_venv(local_path(args.project), args.script, script_args, quiet=args.quiet)
            native = find_native_cli(args.winapp or override)
            return run_with_identity(local_path(args.project), args.script, script_args, native, args.manifest,
                                     python_option=args.python, requirements_option=args.requirements,
                                     verbose=args.verbose, quiet=args.quiet)

        with open(os.devnull, "w", encoding="utf-8") if args.quiet else nullcontext() as output:
            with redirect_stdout(output) if args.quiet else nullcontext():
                if args.command == "identity":
                    return clear_identity(local_path(args.project))
                project = local_path(args.project)
                if args.python is not None:
                    ensure_project_python(project, args.python, verbose=args.verbose)
                if args.command == "generate":
                    config = load_config(project)
                    packages_hash(project)
                    _bindings_after_sdk(config.project, verbose=args.verbose, force=args.force, python_option=args.python)
                else:
                    config = try_load_config(project)
                    if config is not None:
                        packages_hash(project)
                    native = find_native_cli(args.winapp or override)
                    if args.command == "restore":
                        native_args = [str(native), "restore", str(project)]
                    else:
                        native_args = [str(native), "update", "--setup-sdks", args.channel]
                    action = "Restoring" if args.command == "restore" else "Updating"
                    with Workflow(f"{action} {'Python project' if config is not None else 'SDK packages'}",
                                  verbose=args.verbose) as workflow:
                        with workflow.step("Restore SDKs and projections" if args.command == "restore" else "Update SDK packages",
                                           "SDK packages ready"):
                            run_tool(native_args, project, verbose=args.verbose)
                        if args.command == "update" and config is not None:
                            with workflow.step("Restore updated SDKs and projections", "SDKs and projections ready"):
                                run_tool([str(native), "restore", str(project)], project, verbose=args.verbose)
                        _bindings_after_sdk(
                            project, verbose=args.verbose, python_option=args.python, progress=workflow,
                            upgrade_codegen=args.command == "update",
                        )
                        workflow.succeed("Python bindings ready." if config is not None else "SDK packages ready.")
        return 0
    except (WinappError, OSError, UnicodeError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 1
    except KeyboardInterrupt:
        print("Cancelled.", file=sys.stderr)
        return 130
