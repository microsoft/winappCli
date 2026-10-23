# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

from contextlib import redirect_stderr, redirect_stdout
import io
import os
from pathlib import Path
import subprocess
import sys
import zipfile
from unittest.mock import patch

from packaging.version import Version

from winappcli.cli import _prepare_codegen, main
from winappcli.errors import WinappError
from winappcli.tools import find_codegen, find_native_cli, forward_native, run_tool

from helpers import ProjectTest


class ToolTests(ProjectTest):
    def setUp(self):
        super().setUp()
        self.scripts = self.root / "venv Scripts"
        self.scripts.mkdir()
        self.codegen = self.scripts / "dynwinrt-codegen.exe"
        self.codegen.write_bytes(b"fake executable")
        self.python = self.scripts / "python.exe"
        self.native = self.root / "native winapp.exe"
        self.native.write_bytes(b"fake executable")
        bundle = patch("winappcli.tools.bundled_cli", return_value=None)
        bundle.start()
        self.addCleanup(bundle.stop)

    def test_codegen_uses_project_interpreter_scripts_directory(self):
        with (
            patch("winappcli.tools.installed_codegen_version", return_value=Version("0.1.0rc21")),
            patch("winappcli.tools.run_tool", return_value=(
                "lang.py\nselector.namespace-class\ninput.winmd-list\ninput.ref-list\n"
            )),
        ):
            self.assertEqual(find_codegen(self.project, Version("0.1.0rc21"), self.python), self.codegen)

    def test_codegen_version_mismatch_has_exact_install_hint(self):
        with (
            patch("winappcli.tools.installed_codegen_version", return_value=Version("0.1.0rc20")),
            self.assertRaisesRegex(WinappError, "dynwinrt-codegen==0.1.0rc21"),
        ):
            find_codegen(self.project, Version("0.1.0rc21"), self.python)

    def test_codegen_missing_does_not_fall_back_to_path(self):
        self.codegen.unlink()
        with patch("winappcli.tools.shutil.which") as search:
            with self.assertRaisesRegex(WinappError, "project environment"):
                find_codegen(self.project, Version("0.1.0rc21"), self.python)
        search.assert_not_called()

    def test_codegen_repair_hint_uses_the_tool_instead_of_ambient_python(self):
        self.codegen.unlink()
        with self.assertRaises(WinappError) as failure:
            find_codegen(self.project, Version("0.1.0rc21"), self.python)
        self.assertIn("winapp restore", str(failure.exception))
        self.assertIn(str(self.project), str(failure.exception))
        self.assertIn("--python", str(failure.exception))
        self.assertNotIn("python -m pip", str(failure.exception))

    def test_codegen_requires_list_file_support(self):
        with (
            patch("winappcli.tools.installed_codegen_version", return_value=Version("0.1.0rc21")),
            patch("winappcli.tools.run_tool", return_value="something-else"),
            self.assertRaisesRegex(WinappError, "capabilities"),
        ):
            find_codegen(self.project, Version("0.1.0rc21"), self.python)

    def test_native_path_override_resolves_without_running_the_cli(self):
        with patch("winappcli.tools.run_tool") as run:
            found = find_native_cli(str(self.native))
        self.assertEqual(found, self.native)
        run.assert_not_called()

    def test_native_environment_override(self):
        with patch.dict(os.environ, {"WINAPP_CLI_PATH": str(self.native)}):
            self.assertEqual(find_native_cli(None), self.native)

    def test_bundled_cli_is_used_before_path_without_environment_override(self):
        with (
            patch.dict(os.environ, {"WINAPP_CLI_PATH": ""}),
            patch("winappcli.tools.bundled_cli", return_value=self.native),
            patch("winappcli.tools.shutil.which") as search,
        ):
            self.assertEqual(find_native_cli(None), self.native)
        search.assert_not_called()

    def test_explicit_override_wins_over_environment_and_bundle(self):
        with (
            patch.dict(os.environ, {"WINAPP_CLI_PATH": "other.exe"}),
            patch("winappcli.tools.bundled_cli") as bundle,
        ):
            self.assertEqual(find_native_cli(str(self.native)), self.native)
        bundle.assert_not_called()

    def test_source_installation_can_find_cli_on_path(self):
        with (
            patch.dict(os.environ, {"WINAPP_CLI_PATH": ""}),
            patch("winappcli.tools.shutil.which", return_value=str(self.native)),
        ):
            self.assertEqual(find_native_cli(None), self.native)

    def test_broken_bundle_does_not_silently_use_path(self):
        with (
            patch.dict(os.environ, {"WINAPP_CLI_PATH": ""}),
            patch("winappcli.tools.bundled_cli", side_effect=WinappError("Bundled CLI is missing")),
            patch("winappcli.tools.shutil.which") as search,
            self.assertRaisesRegex(WinappError, "Bundled CLI"),
        ):
            find_native_cli(None)
        search.assert_not_called()

    def test_process_failure_surfaces_output(self):
        result = subprocess.CompletedProcess(["fake.exe"], 9, "real diagnostic")
        with patch("winappcli.tools.subprocess.run", return_value=result) as launch:
            with self.assertRaisesRegex(WinappError, "exit 9.*\nreal diagnostic"):
                run_tool(["fake.exe", "an argument with spaces"], self.project)
        self.assertEqual(launch.call_args.args[0], ["fake.exe", "an argument with spaces"])
        self.assertNotIn("shell", launch.call_args.kwargs)

    def test_verbose_output_does_not_echo_dependency_credentials(self):
        arguments = ["fake.exe", "privatepkg @ https://user:CANARY_TOKEN@example.invalid/pkg.whl"]
        result = subprocess.CompletedProcess(arguments, 0, "installer diagnostic\n")
        output = io.StringIO()
        with patch("winappcli.tools.subprocess.run", return_value=result) as launch:
            with redirect_stdout(output):
                run_tool(arguments, self.project, verbose=True)
        self.assertEqual(launch.call_args.args[0], arguments)
        self.assertNotIn("CANARY_TOKEN", output.getvalue())
        self.assertIn("installer diagnostic", output.getvalue())

    def test_missing_process_is_actionable(self):
        with patch("winappcli.tools.subprocess.run", side_effect=FileNotFoundError("missing")):
            with self.assertRaisesRegex(WinappError, "Could not start"):
                run_tool(["missing.exe"], self.project)

    def test_generic_native_forwarding_keeps_streams_and_exit_status(self):
        with patch("winappcli.tools.subprocess.run",
                   return_value=subprocess.CompletedProcess([], 37)) as launch:
            self.assertEqual(
                forward_native(self.native, ["package", "--help"], self.project), 37,
            )
        self.assertEqual(launch.call_args.args[0], [
            str(self.native), "package", "--help",
        ])
        self.assertEqual(launch.call_args.kwargs["cwd"], self.project)
        self.assertNotIn("capture_output", launch.call_args.kwargs)

    def test_native_cli_cannot_resolve_to_the_python_wrapper_itself(self):
        python_entry = self.root / "winapp.exe"
        python_entry.write_bytes(b"MZ launcher stub")
        with zipfile.ZipFile(python_entry, "a") as launcher:
            launcher.writestr("__main__.py", "from winappcli.cli import main\nmain()\n")
        with self.assertRaisesRegex(WinappError, "Python wrapper"):
            find_native_cli(str(python_entry))

    def test_python_launcher_is_rejected_even_when_copied_or_selected_by_path(self):
        python_entry = self.root / "another-environment.exe"
        python_entry.write_bytes(b"MZ launcher stub")
        with zipfile.ZipFile(python_entry, "a") as launcher:
            launcher.writestr("__main__.py", "from winappcli.cli import main\nmain()\n")
        with (
            patch.dict(os.environ, {"WINAPP_CLI_PATH": str(python_entry)}),
            self.assertRaisesRegex(WinappError, "native build"),
        ):
            find_native_cli(None)
        with (
            patch.dict(os.environ, {"WINAPP_CLI_PATH": ""}),
            patch("winappcli.tools.shutil.which", return_value=str(python_entry)),
            self.assertRaisesRegex(WinappError, "Python wrapper"),
        ):
            find_native_cli(None)
        with self.assertRaisesRegex(WinappError, "Python wrapper"):
            find_native_cli(str(python_entry))

    def test_native_winapp_executable_is_not_rejected_for_sharing_the_command_name(self):
        native = self.root / "winapp.exe"
        native.write_bytes(b"native executable")
        self.assertEqual(find_native_cli(str(native)), native)

    def test_init_forwarding_inherits_stdio_and_does_not_inject_command_options(self):
        arguments = ["init", "some project", "--setup-sdks", "preview", "--use-defaults"]
        with patch("winappcli.tools.subprocess.run", return_value=subprocess.CompletedProcess([], 0)) as launch:
            self.assertEqual(forward_native(self.native, arguments, self.project), 0)
        self.assertEqual(launch.call_args.args[0], [str(self.native), *arguments])
        self.assertEqual(launch.call_args.kwargs["env"]["WINAPP_CLI_CALLER"], "python")
        self.assertNotIn("stdout", launch.call_args.kwargs)
        self.assertNotIn("stderr", launch.call_args.kwargs)
        self.assertNotIn("stdin", launch.call_args.kwargs)
        self.assertNotIn("capture_output", launch.call_args.kwargs)

    def test_native_caller_is_set_without_mutating_the_parent_environment(self):
        with (
            patch.dict(os.environ, {"WINAPP_CLI_CALLER": "parent"}),
            patch("winappcli.tools.subprocess.run", return_value=subprocess.CompletedProcess([], 0, "")) as launch,
        ):
            run_tool(["winapp.exe", "restore", str(self.project)], self.project)
            self.assertEqual(launch.call_args.kwargs["env"]["WINAPP_CLI_CALLER"], "python")
            self.assertEqual(os.environ["WINAPP_CLI_CALLER"], "parent")

            run_tool(["codegen.exe", "capabilities"], self.project)
            run_tool(["pip.exe", "install"], self.project, environment=dict(os.environ))

            forward_native(self.native, ["init", "--use-defaults"], self.project)
            self.assertEqual(os.environ["WINAPP_CLI_CALLER"], "parent")


class CliTests(ProjectTest):
    def setUp(self):
        super().setUp()
        self.stdout = io.StringIO()
        self.stderr = io.StringIO()
        self.project_python = self.project / ".venv" / "Scripts" / "python.exe"
        self.project_python.parent.mkdir(parents=True)
        self.project_python.write_bytes(b"project launcher")
        for patcher in (
            patch("winappcli.cli.sys.platform", "win32"),
            patch("winappcli.cli.platform.python_implementation", return_value="CPython"),
            patch("winappcli.cli.platform.machine", return_value="AMD64"),
        ):
            patcher.start()
            self.addCleanup(patcher.stop)

    def invoke(self, *arguments):
        with redirect_stdout(self.stdout), redirect_stderr(self.stderr):
            return main([*arguments, "--project", str(self.project)])

    def test_generate_does_not_restore_but_synchronizes_existing_project_dependencies(self):
        with patch("winappcli.cli._prepare_codegen", return_value=(Path("codegen.exe"), self.project_python, self.codegen_version)):
            with patch("winappcli.cli.generate", return_value=self.config().output) as generate:
                with patch("winappcli.cli.run_tool") as restore:
                    with patch("winappcli.cli.synchronize_project") as install:
                        self.assertEqual(self.invoke("generate"), 0)
        generate.assert_called_once()
        restore.assert_not_called()
        install.assert_called_once_with(self.project, self.project_python, self.config().output, verbose=False)

    def test_restore_precedes_codegen_and_install(self):
        order = []
        with patch("winappcli.cli._prepare_codegen", return_value=(Path("codegen.exe"), self.project_python, self.codegen_version)):
            with patch("winappcli.cli.find_native_cli", return_value=Path("winapp.exe")):
                with patch("winappcli.cli.run_tool", side_effect=lambda *a, **kw: order.append("restore")) as run:
                    with patch("winappcli.cli.generate", side_effect=lambda *a, **kw: order.append("generate") or self.config().output):
                        with patch("winappcli.cli.synchronize_project", side_effect=lambda *a, **kw: order.append("install")):
                            self.assertEqual(self.invoke("restore"), 0)
        self.assertEqual(order, ["restore", "generate", "install"])
        self.assertEqual(run.call_args.args[0], ["winapp.exe", "restore", str(self.project)])
        self.assertEqual(run.call_args.kwargs, {"verbose": False})

    def test_output_options_before_or_after_commands_keep_python_workflows(self):
        cases = (
            (["--quiet"], False, True), (["-q", "true"], False, True),
            (["--quiet=false"], False, False), (["-q", "FALSE"], False, False),
            (["--verbose"], True, False), (["-v"], True, False),
            (["--verbose", "false"], False, False), (["-v=TRUE"], True, False),
            (["--json=false"], False, False),
        )
        for command in ("restore", "update", "generate"):
            for flags, verbose, quiet in cases:
                for leading in (True, False):
                    arguments = [*flags, command] if leading else [command, *flags]
                    self.stdout.seek(0)
                    self.stdout.truncate()
                    with (
                        self.subTest(command=command, flags=flags, leading=leading),
                        patch("winappcli.cli.find_native_cli", return_value=Path("winapp.exe")),
                        patch("winappcli.cli.run_tool") as native,
                        patch("winappcli.cli.forward_native") as forward,
                        patch("winappcli.cli._bindings_after_sdk",
                              side_effect=lambda *a, **kw: print("Python bindings completed")) as bindings,
                    ):
                        self.assertEqual(self.invoke(*arguments), 0)
                        bindings.assert_called_once()
                        self.assertEqual(bindings.call_args.kwargs["verbose"], verbose)
                        forward.assert_not_called()
                        self.assertEqual(native.call_count, {"restore": 1, "update": 2, "generate": 0}[command])
                        for call in native.call_args_list:
                            self.assertEqual(call.kwargs["verbose"], verbose)
                        if quiet:
                            self.assertEqual(self.stdout.getvalue(), "")
                        else:
                            self.assertIn("Python bindings completed", self.stdout.getvalue())

    def test_python_json_and_conflicting_or_invalid_output_options_fail_before_work(self):
        commands = (
            ["restore"], ["update"], ["generate"], ["identity", "clear"],
            ["run", "--project", str(self.project), "app.py"],
            ["run", "--identity", "--project", str(self.project), "app.py"],
        )
        cases = (
            (["--json"], "--json is not supported"),
            (["--json", "TRUE"], "--json is not supported"),
            (["--quiet", "--verbose"], "Cannot specify both"),
            (["--quiet=bogus"], "expected true or false"),
            (["-v="], "expected true or false"),
        )
        for command in commands:
            for flags, message in cases:
                for leading in (True, False):
                    arguments = [*flags, *command] if leading else [command[0], *flags, *command[1:]]
                    self.stderr.seek(0)
                    self.stderr.truncate()
                    with (
                        self.subTest(command=command, flags=flags, leading=leading),
                        patch("winappcli.cli.find_native_cli") as native,
                        patch("winappcli.cli._bindings_after_sdk") as bindings,
                        patch("winappcli.cli.clear_identity") as clear,
                        patch("winappcli.cli._run_in_project_venv") as ordinary,
                        patch("winappcli.cli.run_with_identity") as identity,
                        self.assertRaises(SystemExit) as failure,
                        redirect_stdout(self.stdout), redirect_stderr(self.stderr),
                    ):
                        main(arguments)
                    self.assertEqual(failure.exception.code, 2)
                    self.assertIn(message, self.stderr.getvalue())
                    native.assert_not_called()
                    bindings.assert_not_called()
                    clear.assert_not_called()
                    ordinary.assert_not_called()
                    identity.assert_not_called()

    def test_quiet_python_workflow_keeps_error_diagnostics(self):
        with (
            patch("winappcli.cli.find_native_cli", return_value=Path("winapp.exe")),
            patch("winappcli.cli.run_tool", side_effect=WinappError("SDK restore failed")),
        ):
            self.assertEqual(self.invoke("--quiet", "restore"), 1)
        self.assertEqual(self.stdout.getvalue(), "")
        self.assertIn("error: SDK restore failed", self.stderr.getvalue())

    def test_identity_clear_output_options_work_at_each_command_level(self):
        for arguments in (
            ["--quiet", "identity", "clear"],
            ["identity", "-q", "clear"],
            ["identity", "clear", "--quiet=true"],
        ):
            with (
                self.subTest(arguments=arguments),
                patch("winappcli.cli.clear_identity", side_effect=lambda *a: print("Cleared identity") or 0) as clear,
                patch("winappcli.cli.find_native_cli") as native,
            ):
                self.assertEqual(self.invoke(*arguments), 0)
                clear.assert_called_once_with(self.project)
                native.assert_not_called()
                self.assertEqual(self.stdout.getvalue(), "")

    def test_restore_failure_stops_generation(self):
        with patch("winappcli.cli._prepare_codegen", return_value=(Path("codegen.exe"), self.project_python, self.codegen_version)):
            with patch("winappcli.cli.find_native_cli", return_value=Path("winapp.exe")):
                with patch("winappcli.cli.run_tool", side_effect=WinappError("restore failed")):
                    with patch("winappcli.cli.generate") as generate:
                        self.assertEqual(self.invoke("restore"), 1)
        generate.assert_not_called()
        self.assertIn("restore failed", self.stderr.getvalue())

    def test_install_failure_returns_nonzero(self):
        with patch("winappcli.cli._prepare_codegen", return_value=(Path("codegen.exe"), self.project_python, self.codegen_version)):
            with patch("winappcli.cli.generate", return_value=self.config().output):
                with patch("winappcli.cli.synchronize_project", side_effect=WinappError("pip failed")):
                    self.assertEqual(self.invoke("generate"), 1)
        self.assertIn("pip failed", self.stderr.getvalue())

    def test_uv_binding_rename_reports_failure_instead_of_ready(self):
        self.write_config(package="renamed_bindings")
        source = self.project / "pyproject.toml"
        source.write_text(
            '[project]\nname="example"\ndependencies=["myapp-winrt"]\n'
            "[tool.uv.sources]\nmyapp-winrt = { path = '.\\.winapp\\python\\myapp_winrt', editable = true }\n"
            + source.read_text(), encoding="utf-8",
        )
        before = source.read_bytes()
        with (
            patch("winappcli.cli._prepare_codegen", return_value=(Path("codegen.exe"), self.project_python, self.codegen_version)),
            patch("winappcli.cli.generate", return_value=self.config().output),
            patch("winappcli.dependencies.uv_executable", return_value="uv.exe"),
            patch("winappcli.dependencies.run_tool") as install,
        ):
            self.assertEqual(self.invoke("generate"), 1)
        install.assert_not_called()
        self.assertIn("renamed_bindings", self.stderr.getvalue())
        self.assertIn("does not match", self.stderr.getvalue())
        self.assertNotIn("Python bindings ready", self.stdout.getvalue())
        self.assertEqual(source.read_bytes(), before)

    def test_prepare_codegen_uses_the_project_pin_and_preserves_configuration(self):
        selected = self.codegen_version
        source = self.project / "pyproject.toml"
        before = source.read_bytes()
        with (
            patch("winappcli.cli.ensure_project_python", return_value=self.project_python),
            patch("winappcli.cli.install_codegen") as install,
            patch("winappcli.cli.installed_codegen_version", return_value=selected),
            patch("winappcli.cli.find_codegen", return_value=Path("codegen.exe")),
        ):
            for _ in range(2):
                self.assertEqual(
                    _prepare_codegen(self.project, selected), (Path("codegen.exe"), self.project_python, selected),
                )
        self.assertEqual(install.call_count, 2)
        install.assert_called_with(self.project, self.project_python, selected, verbose=False)
        self.assertEqual(source.read_bytes(), before)

    def test_missing_latest_codegen_metadata_is_not_replaced_with_a_default_version(self):
        with (
            patch("winappcli.cli.ensure_project_python", return_value=self.project_python),
            patch("winappcli.cli.install_codegen"),
            patch("winappcli.cli.installed_codegen_version", return_value=None),
            patch("winappcli.cli.find_codegen") as find,
            self.assertRaisesRegex(WinappError, "installer did not provide"),
        ):
            _prepare_codegen(self.project, None)
        find.assert_not_called()

    def test_generated_version_uses_the_resolved_project_pin(self):
        selected = self.codegen_version
        source = self.project / "pyproject.toml"
        before = source.read_bytes()
        with (
            patch("winappcli.cli._prepare_codegen", return_value=(Path("codegen.exe"), self.project_python, selected)),
            patch("winappcli.cli.generate", return_value=self.config().output) as generate,
            patch("winappcli.cli.synchronize_project"),
        ):
            self.assertEqual(self.invoke("generate"), 0)
        self.assertEqual(generate.call_args.args[2], selected)
        self.assertEqual(source.read_bytes(), before)

    def test_first_setup_records_the_resolved_version_without_changing_selections(self):
        source = self.project / "pyproject.toml"
        source.write_text(source.read_text().replace(f'codegen-version = "{self.codegen_version}"\n', ""), encoding="utf-8")
        expected = self.config()
        selected = Version("0.1.0rc23")
        with (
            patch("winappcli.cli.ensure_project_python", return_value=self.project_python),
            patch("winappcli.cli.install_codegen") as install,
            patch("winappcli.cli.installed_codegen_version", return_value=selected),
            patch("winappcli.cli.find_codegen", return_value=Path("codegen.exe")),
        ):
            _prepare_codegen(self.project, None)
        install.assert_called_once_with(self.project, self.project_python, None, verbose=False)
        self.assertEqual(self.config().codegen_version, selected)
        self.assertEqual(self.config().types, expected.types)
        self.assertEqual(self.config().scope, expected.scope)

    def test_installer_version_mismatch_does_not_change_project_or_generate(self):
        source = self.project / "pyproject.toml"
        before = source.read_bytes()
        with (
            patch("winappcli.cli.ensure_project_python", return_value=self.project_python),
            patch("winappcli.cli.install_codegen"),
            patch("winappcli.cli.installed_codegen_version", return_value=Version("0.1.0rc23")),
            patch("winappcli.cli.find_codegen") as find,
            self.assertRaisesRegex(WinappError, "project requires"),
        ):
            _prepare_codegen(self.project, self.codegen_version)
        find.assert_not_called()
        self.assertEqual(source.read_bytes(), before)

    def test_restore_and_generate_do_not_resolve_a_missing_pin(self):
        source = self.project / "pyproject.toml"
        source.write_text(source.read_text().replace(f'codegen-version = "{self.codegen_version}"\n', ""), encoding="utf-8")
        for command in ("restore", "generate"):
            with self.subTest(command=command):
                with (
                    patch("winappcli.cli.find_native_cli", return_value=Path("winapp.exe")),
                    patch("winappcli.cli.run_tool"),
                    patch("winappcli.cli._prepare_codegen") as prepare,
                    patch("winappcli.cli.generate") as generate,
                ):
                    self.assertEqual(self.invoke(command), 1)
                prepare.assert_not_called()
                generate.assert_not_called()
                self.assertIn("no codegen-version recorded", self.stderr.getvalue())

    def test_explicit_update_advances_the_project_version_and_uses_it_for_generation(self):
        selected = Version("0.1.0rc23")
        with (
            patch("winappcli.cli.find_native_cli", return_value=Path("winapp.exe")),
            patch("winappcli.cli.run_tool"),
            patch("winappcli.cli.ensure_project_python", return_value=self.project_python),
            patch("winappcli.cli.install_codegen") as install,
            patch("winappcli.cli.installed_codegen_version", return_value=selected),
            patch("winappcli.cli.find_codegen", return_value=Path("codegen.exe")),
            patch("winappcli.cli.generate", return_value=self.config().output) as generate,
            patch("winappcli.cli.synchronize_project"),
        ):
            self.assertEqual(self.invoke("update"), 0)
        install.assert_called_once_with(self.project, self.project_python, None, verbose=False)
        self.assertEqual(self.config().codegen_version, selected)
        self.assertEqual(generate.call_args.args[0].codegen_version, selected)
        self.assertEqual(generate.call_args.args[2], selected)

    def test_failed_update_resolution_preserves_the_project_version(self):
        source = self.project / "pyproject.toml"
        before = source.read_bytes()
        with (
            patch("winappcli.cli.find_native_cli", return_value=Path("winapp.exe")),
            patch("winappcli.cli.run_tool"),
            patch("winappcli.cli.ensure_project_python", return_value=self.project_python),
            patch("winappcli.cli.install_codegen", side_effect=WinappError("package source unavailable")),
            patch("winappcli.cli.generate") as generate,
        ):
            self.assertEqual(self.invoke("update"), 1)
        generate.assert_not_called()
        self.assertEqual(source.read_bytes(), before)
        self.assertIn("package source unavailable", self.stderr.getvalue())

    def test_missing_config_is_an_error_not_a_noop(self):
        (self.project / "pyproject.toml").unlink()
        self.assertEqual(self.invoke("generate"), 1)
        self.assertIn("python-bindings", self.stderr.getvalue())

    def test_restore_without_python_opt_in_only_restores_sdk(self):
        (self.project / "pyproject.toml").unlink()
        with patch("winappcli.cli.find_native_cli", return_value=Path("winapp.exe")):
            with patch("winappcli.cli.run_tool") as native:
                with patch("winappcli.cli.find_codegen") as codegen:
                    self.assertEqual(self.invoke("restore"), 0)
        native.assert_called_once()
        codegen.assert_not_called()
        self.assertIn("no Python bindings configuration", self.stdout.getvalue())

    def test_unconfigured_restore_without_yaml_uses_normal_native_behavior(self):
        (self.project / "pyproject.toml").unlink()
        (self.project / "winapp.yaml").unlink()
        with (
            patch("winappcli.cli.find_native_cli", return_value=Path("winapp.exe")),
            patch("winappcli.cli.run_tool") as native,
            patch("winappcli.cli.ensure_project_python") as environment,
        ):
            self.assertEqual(self.invoke("restore"), 0)
        native.assert_called_once_with(
            ["winapp.exe", "restore", str(self.project)], self.project, verbose=False,
        )
        environment.assert_not_called()

    def test_removed_install_flag_is_rejected(self):
        with self.assertRaises(SystemExit) as failure:
            self.invoke("restore", "--install")
        self.assertEqual(failure.exception.code, 2)

    def test_project_python_option_is_wired_into_every_bindings_workflow(self):
        selected = Path(".custom\\Scripts\\python.exe")
        for command in ("restore", "generate", "update"):
            with self.subTest(command=command):
                with (
                    patch("winappcli.cli.ensure_project_python", return_value=self.project_python),
                    patch("winappcli.cli.find_native_cli", return_value=Path("winapp.exe")),
                    patch("winappcli.cli.run_tool"),
                    patch("winappcli.cli._bindings_after_sdk") as bindings,
                ):
                    self.assertEqual(self.invoke(command, "--python", str(selected)), 0)
                self.assertEqual(bindings.call_args.kwargs["python_option"], selected)

    def test_update_runs_full_native_update_and_restore_before_regeneration(self):
        order = []
        with patch("winappcli.cli.find_native_cli", return_value=Path("winapp.exe")):
            with patch("winappcli.cli.run_tool", side_effect=lambda args, *a, **kw: order.append(args[1])) as run:
                with patch("winappcli.cli._bindings_after_sdk", side_effect=lambda *a, **kw: order.append("generate")):
                    self.assertEqual(self.invoke("update", "--channel", "preview"), 0)
        self.assertEqual(order, ["update", "restore", "generate"])
        self.assertEqual(run.call_args_list[0].args[0],
                         ["winapp.exe", "update", "--setup-sdks", "preview"])
        self.assertEqual(run.call_args_list[1].args[0], ["winapp.exe", "restore", str(self.project)])
        for invocation in run.call_args_list:
            self.assertEqual(invocation.args[1], self.project)
            self.assertEqual(invocation.kwargs, {"verbose": False})

    def test_update_restore_failure_stops_binding_generation_and_installation(self):
        with (
            patch("winappcli.cli.find_native_cli", return_value=Path("winapp.exe")),
            patch("winappcli.cli.run_tool", side_effect=["update succeeded", WinappError("restore failed")]) as native,
            patch("winappcli.cli._bindings_after_sdk") as bindings,
        ):
            self.assertEqual(self.invoke("update"), 1)
        self.assertEqual([call.args[0][1] for call in native.call_args_list], ["update", "restore"])
        bindings.assert_not_called()
        self.assertIn("restore failed", self.stderr.getvalue())

    def test_update_without_python_configuration_does_not_load_codegen(self):
        (self.project / "pyproject.toml").unlink()
        with patch("winappcli.cli.find_native_cli", return_value=Path("winapp.exe")):
            with patch("winappcli.cli.run_tool") as native:
                with patch("winappcli.cli.find_codegen") as codegen:
                    self.assertEqual(self.invoke("update"), 0)
        native.assert_called_once()
        codegen.assert_not_called()

    def test_update_failure_does_not_generate_or_install_bindings(self):
        with patch("winappcli.cli.find_native_cli", return_value=Path("winapp.exe")):
            with patch("winappcli.cli.run_tool", side_effect=WinappError("feed failure")):
                with patch("winappcli.cli.generate") as codegen:
                    self.assertEqual(self.invoke("update"), 1)
        codegen.assert_not_called()
        self.assertIn("feed failure", self.stderr.getvalue())

    def test_native_path_prefix_preserves_command_and_exit_code(self):
        with patch("winappcli.cli.find_native_cli", return_value=Path("winapp.exe")) as locate:
            with patch("winappcli.cli.forward_native", return_value=37) as native:
                self.assertEqual(main(["--winapp", "selected.exe", "cert", "info", "--json"]), 37)
        locate.assert_called_once()
        self.assertEqual(locate.call_args.args[0], "selected.exe")
        locate.assert_called_once_with("selected.exe")
        self.assertEqual(native.call_args.args[1], ["cert", "info", "--json"])

    def test_native_forwarding_keeps_output_options_and_prefix_selection(self):
        for arguments, override, forwarded in (
            (["--quiet", "package", "--help"], None, ["--quiet", "package", "--help"]),
            (["--json", "false", "cert", "info", "--verbose=false"], None,
             ["--json", "false", "cert", "info", "--verbose=false"]),
            (["-v", "--winapp", "chosen.exe", "tool", "--help"], "chosen.exe", ["-v", "tool", "--help"]),
            (["--quiet=false", "--winapp=chosen.exe", "--json", "cert", "info"], "chosen.exe",
             ["--quiet=false", "--json", "cert", "info"]),
            (["--winapp", "chosen.exe", "-q", "package", "--help"], "chosen.exe", ["-q", "package", "--help"]),
        ):
            with (
                self.subTest(arguments=arguments),
                patch("winappcli.cli.find_native_cli", return_value=Path("winapp.exe")) as locate,
                patch("winappcli.cli.forward_native", return_value=37) as native,
            ):
                self.assertEqual(main(arguments), 37)
                locate.assert_called_once_with(override)
                self.assertEqual(native.call_args.args[1], forwarded)

    def test_native_word_is_not_a_wrapper_escape(self):
        arguments = ["native", "cert", "info", "--json"]
        with (
            patch("winappcli.cli.find_native_cli", return_value=Path("winapp.exe")),
            patch("winappcli.cli.forward_native", return_value=2) as native,
        ):
            self.assertEqual(main(arguments), 2)
        self.assertEqual(native.call_args.args[1], arguments)

    def test_other_native_commands_are_forwarded_without_python_config(self):
        (self.project / "pyproject.toml").unlink()
        with patch("winappcli.cli.find_native_cli", return_value=Path("winapp.exe")):
            with patch("winappcli.cli.forward_native", return_value=0) as native:
                self.assertEqual(main(["package", "--help"]), 0)
        self.assertEqual(native.call_args.args[1], ["package", "--help"])

    def test_native_run_application_options_do_not_select_wrapper_commands(self):
        for argument in ("--help", "-h", "--identity"):
            with self.subTest(argument=argument):
                arguments = ["run", ".\\dist", "--", argument]
                with (
                    patch("winappcli.cli.find_native_cli", return_value=Path("winapp.exe")),
                    patch("winappcli.cli.forward_native", return_value=37) as native,
                    patch("winappcli.cli.run_with_identity") as identity,
                    redirect_stdout(self.stdout), redirect_stderr(self.stderr),
                ):
                    self.assertEqual(main(arguments), 37)
                self.assertEqual(native.call_args.args[1], arguments)
                identity.assert_not_called()

    def test_native_run_inputs_and_options_are_forwarded_unchanged(self):
        for arguments in (
            ["run"],
            ["run", ".\\dist"],
            ["run", ".\\App.exe"],
            ["run", ".\\App.csproj", "-c", "Release"],
            ["run", ".\\App.vcxproj", "--arch", "arm64"],
            ["run", ".\\App.sln", "--project", "App"],
            ["run", "--args", "input.py", ".\\dist"],
            ["run", "--exe", "launcher.py", ".\\dist"],
            ["run", ".\\App.csproj", "--identity"],
            ["run", "--identity", ".\\dist"],
            ["--quiet", "run", ".\\dist"],
            ["--json", "run", ".\\App.exe"],
            ["run", "--json", ".\\dist"],
            ["-v", "false", "run", "--json=true", ".\\App.csproj"],
            ["run", "--quiet", "false", "--args", "input.py", ".\\dist"],
        ):
            with (
                self.subTest(arguments=arguments),
                patch("winappcli.cli.find_native_cli", return_value=Path("winapp.exe")) as locate,
                patch("winappcli.cli.forward_native", return_value=37) as native,
                patch("winappcli.cli._run_in_project_venv") as ordinary,
                patch("winappcli.cli.run_with_identity") as identity,
            ):
                self.assertEqual(main(arguments), 37)
                locate.assert_called_once_with(None)
                self.assertEqual(native.call_args.args[1], arguments)
                ordinary.assert_not_called()
                identity.assert_not_called()

    def test_ordinary_python_run_uses_current_project_venv_and_streams_exit_status(self):
        script = self.project / "app.py"
        script.write_text("print('hi')", encoding="utf-8")
        (self.project / "pyproject.toml").write_text("invalid metadata", encoding="utf-8")
        with (
            patch("winappcli.cli.Path.cwd", return_value=self.project),
            patch("winappcli.cli.subprocess.run", return_value=subprocess.CompletedProcess([], 37)) as launch,
            patch("winappcli.cli.find_native_cli") as native,
            patch("winappcli.cli.run_with_identity") as identity,
            patch("winappcli.cli.ensure_project_python") as environment,
            patch("winappcli.cli.synchronize_project") as install,
            redirect_stdout(self.stdout), redirect_stderr(self.stderr),
        ):
            self.assertEqual(main(["run", ".\\app.py"]), 37)
        launch.assert_called_once_with(
            [str(self.project_python), str(script)], cwd=self.project, check=False,
        )
        self.assertEqual(self.stdout.getvalue(), "")
        self.assertIn(f"Using project .venv: {self.project_python}", self.stderr.getvalue())
        native.assert_not_called()
        identity.assert_not_called()
        environment.assert_not_called()
        install.assert_not_called()

    def test_ordinary_python_json_stdout_is_not_prefixed_by_wrapper_status(self):
        script = self.project / "app.py"
        script.write_text("", encoding="utf-8")
        payload = '{"result": 42}\n'

        def run_script(*arguments, **kwargs):
            print(payload, end="")
            return subprocess.CompletedProcess([], 37)

        with (
            patch("winappcli.cli.subprocess.run", side_effect=run_script),
            redirect_stdout(self.stdout), redirect_stderr(self.stderr),
        ):
            self.assertEqual(main(["run", "--project", str(self.project), "app.py"]), 37)
        self.assertEqual(self.stdout.getvalue(), payload)
        self.assertIn("Using project .venv:", self.stderr.getvalue())

    def test_quiet_python_run_preserves_application_streams_and_exit_status(self):
        (self.project / "app.py").write_text("", encoding="utf-8")

        def run_script(*arguments, **kwargs):
            print('{"result": 42}')
            print("application error", file=sys.stderr)
            return subprocess.CompletedProcess([], 37)

        for arguments in (
            ["--quiet", "run"], ["run", "-q"], ["-q", "TRUE", "run"],
        ):
            self.stdout.seek(0)
            self.stdout.truncate()
            self.stderr.seek(0)
            self.stderr.truncate()
            with (
                self.subTest(arguments=arguments),
                patch("winappcli.cli.subprocess.run", side_effect=run_script) as launch,
                patch("winappcli.cli.forward_native") as native,
                redirect_stdout(self.stdout), redirect_stderr(self.stderr),
            ):
                self.assertEqual(main([*arguments, "--project", str(self.project), "app.py"]), 37)
                self.assertEqual(self.stdout.getvalue(), '{"result": 42}\n')
                self.assertEqual(self.stderr.getvalue(), "application error\n")
                self.assertNotIn("stdout", launch.call_args.kwargs)
                self.assertNotIn("stderr", launch.call_args.kwargs)
                native.assert_not_called()

    def test_python_run_output_options_are_forwarded_to_identity_preparation(self):
        for flags, verbose, quiet in (
            (["--quiet"], False, True), (["-v", "TRUE"], True, False),
            (["--quiet=false", "--verbose=false"], False, False),
        ):
            for leading in (True, False):
                arguments = [*flags, "run"] if leading else ["run", *flags]
                with (
                    self.subTest(flags=flags, leading=leading),
                    patch("winappcli.cli.find_native_cli", return_value=Path("winapp.exe")),
                    patch("winappcli.cli.run_with_identity", return_value=37) as identity,
                    redirect_stdout(self.stdout), redirect_stderr(self.stderr),
                ):
                    self.assertEqual(main([
                        *arguments, "--identity", "--project", str(self.project), "app.py", "--", "--json",
                    ]), 37)
                    self.assertEqual(identity.call_args.kwargs["verbose"], verbose)
                    self.assertEqual(identity.call_args.kwargs["quiet"], quiet)
                    self.assertEqual(identity.call_args.args[2], ["--json"])

    def test_build_output_directory_named_py_is_still_a_native_input(self):
        directory = self.project / "build.py"
        directory.mkdir()
        arguments = ["run", "--project", str(self.project), str(directory)]
        with (
            patch("winappcli.cli.find_native_cli", return_value=Path("winapp.exe")),
            patch("winappcli.cli.forward_native", return_value=37) as native,
            patch("winappcli.cli._run_in_project_venv") as ordinary,
        ):
            self.assertEqual(main(arguments), 37)
        self.assertEqual(native.call_args.args[1], arguments)
        ordinary.assert_not_called()

    def test_ordinary_python_run_uses_project_root_not_the_scripts_venv(self):
        directory = self.project / "scripts with spaces"
        directory.mkdir()
        script = directory / "app.PY"
        script.write_text("", encoding="utf-8")
        nested_python = directory / ".venv" / "Scripts" / "python.exe"
        nested_python.parent.mkdir(parents=True)
        nested_python.write_bytes(b"not the project environment")
        with (
            patch("winappcli.cli.subprocess.run", return_value=subprocess.CompletedProcess([], 0)) as launch,
            redirect_stdout(self.stdout), redirect_stderr(self.stderr),
        ):
            self.assertEqual(main([
                "run", "--project", str(self.project), str(script.relative_to(self.project)),
            ]), 0)
        self.assertEqual(launch.call_args.args[0], [str(self.project_python), str(script)])
        self.assertEqual(launch.call_args.kwargs["cwd"], self.project)

    def test_ordinary_python_application_flags_stay_with_the_script(self):
        (self.project / "app.py").write_text("", encoding="utf-8")
        for suffix in (
            ["--", "--identity", "--help", "two words"],
            ["--identity", "--help", "two words"],
            ["--", "--project", "elsewhere", "--python", "custom.exe"],
            ["--quiet", "--verbose", "false", "--json", "true"],
            ["--", "-q", "FALSE", "-v=true", "--json=false"],
        ):
            with (
                self.subTest(suffix=suffix),
                patch("winappcli.cli.Path.cwd", return_value=self.project),
                patch("winappcli.cli.subprocess.run", return_value=subprocess.CompletedProcess([], 0)) as launch,
                patch("winappcli.cli.run_with_identity") as identity,
                patch("winappcli.cli.forward_native") as native,
                redirect_stdout(self.stdout), redirect_stderr(self.stderr),
            ):
                self.assertEqual(main(["run", "app.py", *suffix]), 0)
                expected = suffix[1:] if suffix[0] == "--" else suffix
                self.assertEqual(launch.call_args.args[0][2:], expected)
                identity.assert_not_called()
                native.assert_not_called()

    def test_ordinary_uv_project_uses_existing_venv_without_syncing(self):
        (self.project / "app.py").write_text("", encoding="utf-8")
        (self.project / "pyproject.toml").write_text("[tool.uv]\n", encoding="utf-8")
        with (
            patch("winappcli.cli.subprocess.run", return_value=subprocess.CompletedProcess([], 0)) as launch,
            patch("winappcli.cli.run_tool") as tool,
            patch("winappcli.cli.synchronize_project") as sync,
            redirect_stdout(self.stdout), redirect_stderr(self.stderr),
        ):
            self.assertEqual(main(["run", "--project", str(self.project), "app.py"]), 0)
        self.assertEqual(launch.call_args.args[0][0], str(self.project_python))
        tool.assert_not_called()
        sync.assert_not_called()

    def test_missing_project_venv_reports_manual_execution_without_fallback(self):
        (self.project / "app.py").write_text("", encoding="utf-8")
        self.project_python.unlink()
        with (
            patch("winappcli.cli.subprocess.run") as launch,
            patch("winappcli.cli.find_native_cli") as native,
            patch("winappcli.cli.ensure_project_python") as environment,
            redirect_stdout(self.stdout), redirect_stderr(self.stderr),
        ):
            self.assertEqual(main(["run", "--project", str(self.project), "app.py"]), 1)
        self.assertIn(str(self.project_python), self.stderr.getvalue())
        self.assertIn("your own Python interpreter", self.stderr.getvalue())
        self.assertIn("uv run <script>", self.stderr.getvalue())
        launch.assert_not_called()
        native.assert_not_called()
        environment.assert_not_called()

    def test_missing_ordinary_python_script_does_not_start_the_interpreter(self):
        with (
            patch("winappcli.cli.subprocess.run") as launch,
            redirect_stdout(self.stdout), redirect_stderr(self.stderr),
        ):
            self.assertEqual(main(["run", "--project", str(self.project), "missing.py"]), 1)
        self.assertIn("existing project-local .py script", self.stderr.getvalue())
        launch.assert_not_called()

    def test_ordinary_python_script_cannot_escape_the_project(self):
        (self.root / "outside.py").write_text("", encoding="utf-8")
        with (
            patch("winappcli.cli.subprocess.run") as launch,
            redirect_stdout(self.stdout), redirect_stderr(self.stderr),
        ):
            self.assertEqual(main(["run", "--project", str(self.project), "..\\outside.py"]), 1)
        self.assertIn("inside", self.stderr.getvalue())
        launch.assert_not_called()

    def test_ordinary_python_launch_errors_are_explicit(self):
        (self.project / "app.py").write_text("", encoding="utf-8")
        with (
            patch("winappcli.cli.subprocess.run", side_effect=OSError("not executable")),
            redirect_stdout(self.stdout), redirect_stderr(self.stderr),
        ):
            self.assertEqual(main(["run", "--project", str(self.project), "app.py"]), 1)
        self.assertIn("Could not launch", self.stderr.getvalue())
        self.assertIn("not executable", self.stderr.getvalue())

    def test_identity_only_options_are_not_silently_ignored_by_ordinary_run(self):
        for option in ("--python", "--requirements", "--manifest", "--winapp"):
            with (
                self.subTest(option=option),
                patch("winappcli.cli.subprocess.run") as launch,
                patch("winappcli.cli.find_native_cli") as native,
                redirect_stdout(self.stdout), redirect_stderr(self.stderr),
            ):
                self.assertEqual(main([
                    "run", "--project", str(self.project), option, "selected", "app.py",
                ]), 1)
                launch.assert_not_called()
                native.assert_not_called()
                self.assertIn("require --identity", self.stderr.getvalue())

    def test_identity_run_requires_a_script(self):
        with (
            self.assertRaises(SystemExit) as failure,
            redirect_stdout(self.stdout), redirect_stderr(self.stderr),
        ):
            main(["run", "--identity"])
        self.assertEqual(failure.exception.code, 2)
        self.assertIn("required: script", self.stderr.getvalue())

    def test_identity_clear_dispatches_without_generator_or_native_cli(self):
        with patch("winappcli.cli.clear_identity", return_value=0) as clear:
            with patch("winappcli.cli.find_native_cli") as native:
                self.assertEqual(self.invoke("identity", "clear"), 0)
        clear.assert_called_once_with(self.project)
        native.assert_not_called()

    def test_identity_script_args_and_exit_code_are_preserved(self):
        (self.project / "app.py").write_text("print('hi')", encoding="utf-8")
        with patch("winappcli.cli.find_native_cli", return_value=Path("winapp.exe")):
            with patch("winappcli.cli.run_with_identity", return_value=37) as launch:
                self.assertEqual(main([
                    "run", "--identity", "--project", str(self.project),
                    "app.py", "--", "--flag", "two words",
                ]), 37)
        self.assertEqual(launch.call_args.args[2], ["--flag", "two words"])

    def test_identity_short_command_uses_current_project_and_default_python(self):
        with (
            patch("winappcli.cli.Path.cwd", return_value=self.project),
            patch("winappcli.cli.find_native_cli", return_value=Path("winapp.exe")),
            patch("winappcli.cli.run_with_identity", return_value=0) as launch,
        ):
            self.assertEqual(main(["run", "--identity", ".\\app.py"]), 0)
        self.assertEqual(launch.call_args.args[0], self.project)
        self.assertIsNone(launch.call_args.kwargs["python_option"])
        self.assertIsNone(launch.call_args.kwargs["requirements_option"])

    def test_identity_project_interpreter_and_requirements_are_forwarded(self):
        with patch("winappcli.cli.find_native_cli", return_value=Path("winapp.exe")):
            with patch("winappcli.cli.run_with_identity", return_value=0) as launch:
                self.assertEqual(main([
                    "run", "--identity", "--project", str(self.project),
                    "--python", ".custom\\Scripts\\python.exe", "--requirements", "deps.txt",
                    "app.py",
                ]), 0)
        self.assertEqual(launch.call_args.kwargs["python_option"], Path(".custom\\Scripts\\python.exe"))
        self.assertEqual(launch.call_args.kwargs["requirements_option"], Path("deps.txt"))

    def test_explicit_project_environment_checked_before_restore(self):
        with patch("winappcli.cli.ensure_project_python", side_effect=WinappError("not a venv")):
            with patch("winappcli.cli.find_native_cli") as native:
                self.assertEqual(self.invoke("restore", "--python", str(self.project_python)), 1)
        native.assert_not_called()

    def test_non_windows_reports_supported_platform(self):
        with patch("winappcli.cli.sys.platform", "linux"):
            self.assertEqual(self.invoke("generate"), 1)
        self.assertIn("Windows", self.stderr.getvalue())

    def test_redirected_output_handles_unicode_project_paths(self):
        project = self.root / "example-\u4f8b"
        project.mkdir()
        result = subprocess.run(
            [sys.executable, "-m", "winappcli", "generate", "--project", str(project)],
            capture_output=True, text=True, encoding="utf-8",
            env={**os.environ, "PYTHONIOENCODING": "ascii"},
        )
        self.assertEqual(result.returncode, 1)
        self.assertIn(str(project), result.stderr)
        self.assertNotIn("Traceback", result.stderr)
