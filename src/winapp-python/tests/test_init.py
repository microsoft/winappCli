# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

from contextlib import redirect_stderr, redirect_stdout
import io
import json
import os
from pathlib import Path
from unittest.mock import patch

from winapp.cli import main
from winapp.dependencies import detect_dependencies, register_dependencies
from winapp.errors import WinappError

from helpers import ProjectTest


class NativeInitTests(ProjectTest):
    def setUp(self):
        super().setUp()
        self.stdout = io.StringIO()
        self.stderr = io.StringIO()
        for patcher in (
            patch("winapp.cli.Path.cwd", return_value=self.project),
            patch("winapp.cli.find_native_cli", return_value=Path("winapp.exe")),
            patch("winapp.cli.sys.platform", "win32"),
            patch("winapp.cli.platform.python_implementation", return_value="CPython"),
            patch("winapp.cli.platform.machine", return_value="AMD64"),
            patch("winapp.cli.sys.stdin.isatty", return_value=False),
            patch.dict(os.environ, {"CI": ""}),
        ):
            patcher.start()
            self.addCleanup(patcher.stop)

    def native(self, executable, arguments, working_directory):
        return 0

    def invoke(self, *arguments):
        with redirect_stdout(self.stdout), redirect_stderr(self.stderr):
            return main(["init", *arguments])

    def unconfigured(self):
        (self.project / "pyproject.toml").write_text('[project]\nname = "Example-App"\n', encoding="utf-8")

    def test_bare_init_preserves_native_argv_and_existing_config_without_prompt(self):
        with (
            patch("winapp.cli.forward_native", side_effect=self.native) as native,
            patch("winapp.cli._configure_after_init") as setup,
            patch("builtins.input") as prompt,
        ):
            self.assertEqual(self.invoke(), 0)
        self.assertEqual(native.call_args.args[:3], (Path("winapp.exe"), ["init"], self.project))
        setup.assert_called_once_with(self.project, reset=False, config_only=False, verbose=False)
        prompt.assert_not_called()

    def test_noninteractive_new_project_requires_explicit_opt_in(self):
        for flags in ([], ["--use-defaults"], ["--no-prompt"]):
            with self.subTest(flags=flags):
                self.unconfigured()
                before = (self.project / "pyproject.toml").read_bytes()
                with (
                    patch("winapp.cli.forward_native", side_effect=self.native),
                    patch("winapp.cli._configure_after_init") as setup,
                ):
                    self.assertEqual(self.invoke(*flags), 0)
                setup.assert_not_called()
                self.assertEqual((self.project / "pyproject.toml").read_bytes(), before)

    def test_interactive_new_project_confirmation_defaults_to_yes_and_retries_invalid_answers(self):
        self.unconfigured()
        with (
            patch("winapp.cli.sys.stdin.isatty", return_value=True),
            patch("winapp.cli.forward_native", side_effect=self.native),
            patch("builtins.input", side_effect=["invalid", ""]) as prompt,
            patch("winapp.cli._configure_after_init") as setup,
        ):
            self.assertEqual(self.invoke(str(self.project)), 0)
        self.assertEqual(prompt.call_count, 2)
        setup.assert_called_once()

    def test_interactive_no_makes_no_bindings_changes(self):
        self.unconfigured()
        with (
            patch("winapp.cli.sys.stdin.isatty", return_value=True),
            patch("winapp.cli.forward_native", side_effect=self.native),
            patch("builtins.input", return_value="no"),
            patch("winapp.cli._configure_after_init") as setup,
        ):
            self.assertEqual(self.invoke(), 0)
        setup.assert_not_called()

    def test_explicit_opt_in_only_strips_its_own_flag(self):
        arguments = [
            str(self.project), "--setup-sdks", "preview", "--use-defaults",
            "--config-dir=" + str(self.project), "--no-gitignore", "--verbose",
        ]
        with (
            patch("winapp.cli.forward_native", side_effect=self.native) as native,
            patch("winapp.cli._configure_after_init") as setup,
        ):
            self.assertEqual(self.invoke(*arguments, "--add-python-bindings"), 0)
        self.assertEqual(native.call_args.args[1], ["init", *arguments])
        setup.assert_called_once_with(self.project, reset=False, config_only=False, verbose=True)

    def test_leading_output_options_do_not_bypass_python_setup(self):
        for flags, verbose in (
            (["--quiet"], False), (["-q", "false"], False),
            (["--verbose=true"], True), (["-v"], True),
        ):
            with (
                self.subTest(flags=flags),
                patch("winapp.cli.forward_native", side_effect=self.native) as native,
                patch("winapp.cli._configure_after_init") as setup,
                redirect_stdout(self.stdout), redirect_stderr(self.stderr),
            ):
                self.assertEqual(main([*flags, "init", "--use-defaults"]), 0)
                self.assertEqual(native.call_args.args[1], [*flags, "init", "--use-defaults"])
                setup.assert_called_once_with(self.project, reset=False, config_only=False, verbose=verbose)

    def test_explicit_opt_in_requires_noninteractive_setup_and_rejects_no_sdk_modes(self):
        for arguments in (["--sparse"], ["--setup-sdks", "none"], ["--setup-sdks=none"], []):
            with self.subTest(arguments=arguments):
                with (
                    patch("winapp.cli.sys.stdin.isatty", return_value=True),
                    patch("winapp.cli.forward_native") as native,
                ):
                    self.assertEqual(self.invoke(*arguments, "--add-python-bindings"), 1)
                native.assert_not_called()

    def test_existing_defaults_and_explicit_opt_in_preserve_metadata(self):
        for flags in (["--use-defaults"], ["--no-prompt"], ["--use-defaults", "--add-python-bindings"]):
            with self.subTest(flags=flags):
                with (
                    patch("winapp.cli.forward_native", side_effect=self.native),
                    patch("winapp.cli._configure_after_init") as setup,
                ):
                    self.assertEqual(self.invoke(*flags), 0)
                self.assertFalse(setup.call_args.kwargs["reset"])

    def test_config_only_defaults_keep_the_existing_bindings_table(self):
        self.write_config(package="custom_winrt", types=["Windows.Foundation.Uri"], scope="types")
        source = self.project / "pyproject.toml"
        before = source.read_bytes()
        expected = self.config()

        def native(executable, arguments, working_directory):
            return 0

        with (
            patch("winapp.cli.forward_native", side_effect=native),
            patch("winapp.cli._prepare_codegen"),
        ):
            self.assertEqual(self.invoke("--use-defaults", "--config-only"), 0)
        self.assertEqual(source.read_bytes(), before)
        self.assertEqual(self.config(), expected)

    def test_existing_metadata_can_still_be_reset_with_explicit_confirmation(self):
        with (
            patch("winapp.cli.sys.stdin.isatty", return_value=True),
            patch("winapp.cli.forward_native", side_effect=self.native),
            patch("builtins.input", return_value="yes") as prompt,
            patch("winapp.cli._configure_after_init") as setup,
        ):
            self.assertEqual(self.invoke(str(self.project)), 0)
        prompt.assert_called_once()
        self.assertTrue(setup.call_args.kwargs["reset"])

    def test_explicit_false_preserves_interactive_reset_confirmation(self):
        for flags in (
            ["--use-defaults", "false"], ["--no-prompt", "FALSE"],
            ["--use-defaults=false"], ["--no-prompt=false"],
        ):
            with self.subTest(flags=flags):
                with (
                    patch("winapp.cli.sys.stdin.isatty", return_value=True),
                    patch("winapp.cli.forward_native", side_effect=self.native) as native,
                    patch("builtins.input", return_value="n") as prompt,
                    patch("winapp.cli._configure_after_init") as setup,
                ):
                    self.assertEqual(self.invoke(str(self.project), *flags), 0)
                prompt.assert_called_once()
                setup.assert_called_once_with(self.project, reset=False, config_only=False, verbose=False)
                self.assertEqual(native.call_args.args[1], ["init", str(self.project), *flags])

    def test_equals_true_uses_defaults_without_prompting(self):
        for flag in ("--use-defaults=true", "--no-prompt=TRUE"):
            with self.subTest(flag=flag):
                with (
                    patch("winapp.cli.sys.stdin.isatty", return_value=True),
                    patch("winapp.cli.forward_native", side_effect=self.native),
                    patch("builtins.input", return_value="n") as prompt,
                    patch("winapp.cli._configure_after_init") as setup,
                ):
                    self.assertEqual(self.invoke(flag), 0)
                prompt.assert_not_called()
                self.assertFalse(setup.call_args.kwargs["reset"])

    def test_explicit_false_quiet_and_verbose_do_not_override_python_output(self):
        with (
            patch("winapp.cli.forward_native", side_effect=self.native),
            patch("winapp.cli._configure_after_init", side_effect=lambda *a, **kw: print("Python output")) as setup,
        ):
            self.assertEqual(self.invoke("--quiet", "false", "--verbose", "false"), 0)
        self.assertIn("Python output", self.stdout.getvalue())
        self.assertFalse(setup.call_args.kwargs["verbose"])

    def test_explicit_false_sparse_does_not_block_bindings(self):
        with (
            patch("winapp.cli.forward_native", side_effect=self.native) as native,
            patch("winapp.cli._configure_after_init") as setup,
        ):
            self.assertEqual(self.invoke("--sparse", "false", "--add-python-bindings"), 0)
        setup.assert_called_once()
        self.assertEqual(native.call_args.args[1], ["init", "--sparse", "false"])

    def test_ci_never_prompts_even_for_existing_config(self):
        with (
            patch("winapp.cli.sys.stdin.isatty", return_value=True),
            patch.dict(os.environ, {"CI": "true"}),
            patch("winapp.cli.forward_native", side_effect=self.native),
            patch("builtins.input") as prompt,
            patch("winapp.cli._configure_after_init"),
        ):
            self.assertEqual(self.invoke(), 0)
        prompt.assert_not_called()

    def test_python_flags_other_than_opt_in_are_forwarded_to_native_validation(self):
        arguments = ["--install", "--python", "other.exe", "--package", "other_winrt", "--type", "Uri"]
        with (
            patch("winapp.cli.forward_native", return_value=2) as native,
            patch("winapp.cli._configure_after_init") as setup,
        ):
            self.assertEqual(self.invoke(*arguments), 2)
        self.assertEqual(native.call_args.args[1], ["init", *arguments])
        setup.assert_not_called()

    def test_native_failure_is_returned_before_config_or_dependency_changes(self):
        source = self.project / "pyproject.toml"
        source.write_text("not valid TOML", encoding="utf-8")
        with (
            patch("winapp.cli.forward_native", return_value=37),
            patch("winapp.cli._configure_after_init") as setup,
        ):
            self.assertEqual(self.invoke("--new-native-option"), 37)
        self.assertEqual(source.read_text(), "not valid TOML")
        setup.assert_not_called()

    def test_setup_uses_the_native_selected_project_not_the_callers_cwd(self):
        selected = self.project / "native-selected project"
        selected.mkdir()
        (selected / "winapp.yaml").write_bytes((self.project / "winapp.yaml").read_bytes())
        (selected / ".winapp").mkdir()
        (selected / ".winapp" / "winmds.lock.json").write_text(json.dumps(self.inventory), encoding="utf-8")

        def native(executable, arguments, working_directory):
            self.assertEqual(working_directory, self.project)
            return 0

        with (
            patch("winapp.cli.sys.stdin.isatty", return_value=True),
            patch("winapp.cli.forward_native", side_effect=native),
            patch("builtins.input", side_effect=["2", "yes"]),
            patch("winapp.cli._configure_after_init") as setup,
        ):
            self.assertEqual(self.invoke(), 0)
        self.assertEqual(setup.call_args.args[0], selected)

    def test_interactive_artifacts_are_only_candidates_and_need_confirmation(self):
        for answer, expected_calls in (("no", 0), ("yes", 1)):
            with (
                self.subTest(answer=answer),
                patch("winapp.cli.sys.stdin.isatty", return_value=True),
                patch("winapp.cli.forward_native", side_effect=self.native),
                patch("builtins.input", return_value=answer) as prompt,
                patch("winapp.cli._configure_after_init") as setup,
            ):
                self.assertEqual(self.invoke(), 0)
                prompt.assert_called_once()
                self.assertIn(str(self.project), prompt.call_args.args[0])
                self.assertEqual(setup.call_count, expected_calls)

    def test_multiple_candidates_can_be_declined_without_modifying_projects(self):
        child = self.project / "other"
        child.mkdir()
        (child / "winapp.yaml").write_bytes((self.project / "winapp.yaml").read_bytes())
        (child / ".winapp").mkdir()
        (child / ".winapp" / "winmds.lock.json").write_text(json.dumps(self.inventory), encoding="utf-8")
        with (
            patch("winapp.cli.sys.stdin.isatty", return_value=True),
            patch("winapp.cli.forward_native", side_effect=self.native),
            patch("builtins.input", side_effect=["invalid", "0"]),
            patch("winapp.cli._configure_after_init") as setup,
        ):
            self.assertEqual(self.invoke(), 0)
        setup.assert_not_called()
        self.assertIn("Enter one of the listed numbers", self.stdout.getvalue())

    def test_eof_during_artifact_confirmation_is_an_explicit_error(self):
        with (
            patch("winapp.cli.sys.stdin.isatty", return_value=True),
            patch("winapp.cli.forward_native", side_effect=self.native),
            patch("builtins.input", side_effect=EOFError),
            patch("winapp.cli._configure_after_init") as setup,
        ):
            self.assertEqual(self.invoke(), 1)
        setup.assert_not_called()
        self.assertIn("Input ended", self.stderr.getvalue())

    def test_no_ready_artifacts_do_not_enable_bindings_from_a_stale_configuration(self):
        (self.project / ".winapp" / "winmds.lock.json").unlink()
        with (
            patch("winapp.cli.sys.stdin.isatty", return_value=True),
            patch("winapp.cli.forward_native", side_effect=self.native),
            patch("builtins.input") as prompt,
            patch("winapp.cli._configure_after_init") as setup,
        ):
            self.assertEqual(self.invoke(), 1)
        prompt.assert_not_called()
        setup.assert_not_called()
        self.assertIn("no usable project-local SDK", self.stderr.getvalue())

    def test_config_only_interactive_target_requires_confirmation_even_without_new_files(self):
        (self.project / ".winapp" / "winmds.lock.json").unlink()
        with (
            patch("winapp.cli.sys.stdin.isatty", return_value=True),
            patch("winapp.cli.forward_native", side_effect=self.native),
            patch("builtins.input", return_value="yes") as prompt,
            patch("winapp.cli._configure_after_init") as setup,
        ):
            self.assertEqual(self.invoke("--config-only"), 0)
        prompt.assert_called_once()
        self.assertTrue(setup.call_args.kwargs["config_only"])

    def test_quiet_ambiguous_target_does_not_guess_or_prompt(self):
        with (
            patch("winapp.cli.sys.stdin.isatty", return_value=True),
            patch("winapp.cli.forward_native", side_effect=self.native),
            patch("builtins.input") as prompt,
            patch("winapp.cli._configure_after_init") as setup,
        ):
            self.assertEqual(self.invoke("--quiet"), 1)
        prompt.assert_not_called()
        setup.assert_not_called()
        self.assertIn("Specify a directory", self.stderr.getvalue())

    def test_explicit_directory_with_colon_defaults_does_not_prompt_or_reset(self):
        with (
            patch("winapp.cli.sys.stdin.isatty", return_value=True),
            patch("winapp.cli.forward_native", side_effect=self.native),
            patch("builtins.input") as prompt,
            patch("winapp.cli._configure_after_init") as setup,
        ):
            self.assertEqual(self.invoke(str(self.project), "--use-defaults:true"), 0)
        prompt.assert_not_called()
        self.assertFalse(setup.call_args.kwargs["reset"])

    def test_help_and_schema_do_not_generate_and_help_exposes_the_wrapper_flag(self):
        for flag in ("--help", "-h", "-?", "/?", "--cli-schema", "--version"):
            with self.subTest(flag=flag):
                with (
                    patch("winapp.cli.forward_native", return_value=0) as native,
                    patch("winapp.cli._configure_after_init") as setup,
                ):
                    self.assertEqual(self.invoke(flag), 0)
                self.assertEqual(native.call_args.args[1], ["init", flag])
                setup.assert_not_called()
        self.assertIn("--add-python-bindings", self.stdout.getvalue())

    def test_native_help_aliases_skip_opt_in_validation_and_append_python_option(self):
        for flag in ("--help", "-h", "-?", "/?"):
            with self.subTest(flag=flag):
                self.stdout.seek(0)
                self.stdout.truncate()
                with (
                    patch("winapp.cli.sys.stdin.isatty", return_value=True),
                    patch("winapp.cli.forward_native", return_value=0) as native,
                    patch("winapp.cli._configure_after_init") as setup,
                ):
                    self.assertEqual(self.invoke("--add-python-bindings", flag), 0)
                self.assertEqual(native.call_args.args[1], ["init", flag])
                self.assertIn("--add-python-bindings", self.stdout.getvalue())
                setup.assert_not_called()

    def test_native_non_sdk_modes_skip_and_explicit_requests_fail_loudly(self):
        for flags in (["--setup-sdks", "none"], ["--sparse"], ["--ignore-config"]):
            with self.subTest(flags=flags):
                with (
                    patch("winapp.cli.forward_native", side_effect=self.native),
                    patch("winapp.cli._configure_after_init") as setup,
                ):
                    self.assertEqual(self.invoke(*flags), 0)
                    self.assertEqual(self.invoke(*flags, "--add-python-bindings"), 1)
                setup.assert_not_called()

    def test_config_only_registers_without_running_generation(self):
        def native(executable, arguments, working_directory):
            return 0
        with (
            patch("winapp.cli.forward_native", side_effect=native),
            patch("winapp.cli._configure_after_init") as setup,
        ):
            self.assertEqual(self.invoke("--config-only", "--add-python-bindings"), 0)
        self.assertTrue(setup.call_args.kwargs["config_only"])

    def test_external_sdk_layout_is_not_guessed_or_written_into(self):
        def native(executable, arguments, working_directory):
            return 0
        with (
            patch("winapp.cli.forward_native", side_effect=native),
            patch("winapp.cli._configure_after_init") as setup,
        ):
            self.assertEqual(self.invoke("--config-dir", str(self.root / "external")), 0)
            self.assertEqual(self.invoke("--config-dir", str(self.root / "external"), "--add-python-bindings"), 1)
        setup.assert_not_called()

    def test_invalid_sdk_inventory_does_not_change_python_files(self):
        for data in ("not JSON", "[]", '{"schema":1}'):
            with self.subTest(data=data):
                def native(executable, arguments, working_directory):
                    (self.project / ".winapp" / "winmds.lock.json").write_text(data, encoding="utf-8")
                    return 0
                with (
                    patch("winapp.cli.forward_native", side_effect=native),
                    patch("winapp.cli._configure_after_init") as setup,
                ):
                    self.assertEqual(self.invoke(), 1)
                setup.assert_not_called()

    def test_native_runs_before_registration_generation_and_installation(self):
        order = []
        def native(*args, **kwargs):
            order.append("native")
            return self.native(*args, **kwargs)
        with (
            patch("winapp.cli.forward_native", side_effect=native),
            patch("winapp.cli._prepare_codegen", return_value=(Path("codegen.exe"), Path("python.exe"), self.codegen_version)),
            patch("winapp.cli.generate", side_effect=lambda *a, **kw: order.append("generate") or self.config().output),
            patch("winapp.cli.register_dependencies", side_effect=lambda *a, **kw: order.append("dependencies")),
            patch("winapp.cli.synchronize_project", side_effect=lambda *a, **kw: order.append("install")),
        ):
            self.assertEqual(self.invoke("--add-python-bindings"), 0)
        self.assertEqual(order, ["native", "generate", "dependencies", "install"])

    def test_failed_codegen_does_not_publish_requirements_or_uv_dependencies(self):
        for manager in ("requirements", "uv"):
            self.write_config()
            included = self.project / "included.txt"
            included.write_text("dynwinrt-codegen==0.1.0rc20\npackaging==26.3\n", encoding="utf-8")
            (self.project / "requirements.txt").write_text("-r included.txt\n", encoding="utf-8")
            source = self.project / "pyproject.toml"
            if manager == "uv":
                source.write_text(
                    '[project]\nname = "Example-App"\ndependencies = ["dynwinrt==0.1.0rc20", "packaging==26.3"]\n'
                    '[tool.uv]\n' + source.read_text(), encoding="utf-8",
                )
            paths = [source, included, self.project / "requirements.txt", self.project / "requirements-dev.txt"]
            before = {path: path.read_bytes() for path in paths}
            for phase in ("prepare", "generate"):
                with (
                    self.subTest(manager=manager, phase=phase),
                    patch("winapp.cli.forward_native", side_effect=self.native),
                    patch("winapp.cli._prepare_codegen",
                          side_effect=WinappError("codegen unavailable") if phase == "prepare" else None,
                          return_value=(Path("codegen.exe"), Path("python.exe"), self.codegen_version)),
                    patch("winapp.cli.generate", side_effect=WinappError("generation failed")) as generate,
                    patch("winapp.cli.register_dependencies", wraps=register_dependencies) as register,
                    patch("winapp.cli.synchronize_project") as install,
                ):
                    self.assertEqual(self.invoke("--add-python-bindings"), 1)
                    self.assertEqual({path: path.read_bytes() for path in paths}, before)
                    self.assertFalse(self.config().output.exists())
                    register.assert_not_called()
                    install.assert_not_called()
                    if phase == "prepare":
                        generate.assert_not_called()

    def test_generated_package_exists_before_dependencies_are_registered_and_synchronized(self):
        for manager in ("requirements", "uv"):
            self.write_config()
            source = self.project / "pyproject.toml"
            if manager == "uv":
                source.write_text(
                    '[project]\nname = "Example-App"\ndependencies = []\n[tool.uv]\n' + source.read_text(),
                    encoding="utf-8",
                )
            output = self.config().output
            requirements_before = (self.project / "requirements.txt").read_bytes()
            metadata_before = source.read_bytes()

            def generate(config, *args, **kwargs):
                self.assertEqual((self.project / "requirements.txt").read_bytes(), requirements_before)
                self.assertEqual(source.read_bytes(), metadata_before)
                output.mkdir(parents=True, exist_ok=True)
                (output / "pyproject.toml").write_text('[project]\nname = "myapp-winrt"\nversion = "0.1.0"\n', encoding="utf-8")
                return output

            def synchronize(project, python, bindings, **kwargs):
                self.assertEqual(bindings, output)
                self.assertTrue((bindings / "pyproject.toml").is_file())
                self.assertEqual(detect_dependencies(project).kind, manager)
                if manager == "uv":
                    self.assertIn("myapp-winrt", source.read_text())
                    self.assertIn("editable = true", source.read_text())
                else:
                    self.assertIn("-e '.\\.winapp\\python\\myapp_winrt'", (project / "requirements.txt").read_text())

            with (
                self.subTest(manager=manager),
                patch("winapp.cli.forward_native", side_effect=self.native),
                patch("winapp.cli._prepare_codegen",
                      return_value=(Path("codegen.exe"), Path("python.exe"), self.codegen_version)),
                patch("winapp.cli.generate", side_effect=generate),
                patch("winapp.cli.synchronize_project", side_effect=synchronize) as install,
            ):
                self.assertEqual(self.invoke("--add-python-bindings"), 0)
                install.assert_called_once()

    def test_failed_config_only_codegen_preserves_dependency_declarations(self):
        source = self.project / "pyproject.toml"
        application = self.project / "requirements.txt"
        application.write_text("dynwinrt==0.1.0rc20\n", encoding="utf-8")
        before = {path: path.read_bytes() for path in (source, application, self.project / "requirements-dev.txt")}

        def native(executable, arguments, working_directory):
            return 0

        with (
            patch("winapp.cli.forward_native", side_effect=native),
            patch("winapp.cli._prepare_codegen", side_effect=WinappError("codegen unavailable")),
            patch("winapp.cli.register_dependencies", wraps=register_dependencies) as register,
            patch("winapp.cli.generate") as generate,
        ):
            self.assertEqual(self.invoke("--config-only", "--add-python-bindings"), 1)
        self.assertEqual({path: path.read_bytes() for path in before}, before)
        register.assert_not_called()
        generate.assert_not_called()

    def test_config_only_registers_dependencies_after_codegen_without_generation(self):
        order = []

        def native(executable, arguments, working_directory):
            return 0

        with (
            patch("winapp.cli.forward_native", side_effect=native),
            patch("winapp.cli._prepare_codegen", side_effect=lambda *a, **kw: order.append("prepare")),
            patch("winapp.cli.register_dependencies", side_effect=lambda *a, **kw: order.append("dependencies")),
            patch("winapp.cli.generate") as generate,
            patch("winapp.cli.synchronize_project") as install,
        ):
            self.assertEqual(self.invoke("--config-only", "--add-python-bindings"), 0)
        self.assertEqual(order, ["prepare", "dependencies"])
        generate.assert_not_called()
        install.assert_not_called()

    def test_new_init_records_the_short_default_name_without_resetting_custom_packages(self):
        self.unconfigured()
        with (
            patch("winapp.cli.forward_native", side_effect=self.native),
            patch("winapp.cli._bindings_after_sdk"),
        ):
            self.assertEqual(self.invoke("--add-python-bindings"), 0)
        self.assertEqual(self.config().package, "winapp_bindings")
        self.write_config(package="custom_winrt")
        with (
            patch("winapp.cli.forward_native", side_effect=self.native),
            patch("winapp.cli._bindings_after_sdk"),
        ):
            self.assertEqual(self.invoke("--add-python-bindings"), 0)
        self.assertEqual(self.config().package, "custom_winrt")

    def test_existing_init_uses_the_recorded_generator_without_advancing_it(self):
        with (
            patch("winapp.cli.forward_native", side_effect=self.native),
            patch("winapp.cli._bindings_after_sdk") as bindings,
        ):
            self.assertEqual(self.invoke("--use-defaults"), 0)
        self.assertFalse(bindings.call_args.kwargs["upgrade_codegen"])
        self.assertEqual(self.config().codegen_version, self.codegen_version)

    def test_init_does_not_add_dynwinrt_or_codegen_project_dependencies(self):
        self.unconfigured()
        with (
            patch("winapp.cli.forward_native", side_effect=self.native),
            patch("winapp.cli._bindings_after_sdk"),
        ):
            self.assertEqual(self.invoke("--add-python-bindings"), 0)
        text = (self.project / "pyproject.toml").read_text()
        self.assertNotIn("dynwinrt", text)
        self.assertNotIn("dependency-groups", text)

    def test_setup_failure_returns_nonzero_after_native_success(self):
        with (
            patch("winapp.cli.forward_native", side_effect=self.native),
            patch("winapp.cli._configure_after_init", side_effect=WinappError("setup failed")),
        ):
            self.assertEqual(self.invoke(), 1)
        self.assertIn("setup failed", self.stderr.getvalue())

    def test_quiet_only_suppresses_python_output_not_native_streams(self):
        def native(*args, **kwargs):
            print("native output")
            return self.native(*args, **kwargs)
        with (
            patch("winapp.cli.forward_native", side_effect=native),
            patch("winapp.cli._configure_after_init", side_effect=lambda *a, **kw: print("Python output")),
        ):
            self.assertEqual(self.invoke("--quiet"), 0)
        self.assertIn("native output", self.stdout.getvalue())
        self.assertNotIn("Python output", self.stdout.getvalue())
