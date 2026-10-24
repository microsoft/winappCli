# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

from contextlib import redirect_stderr, redirect_stdout
import io
from pathlib import Path
from unittest.mock import patch

from winappcli.cli import main
from winappcli.errors import WinappError

from helpers import ProjectTest


class HelpTests(ProjectTest):
    def setUp(self):
        super().setUp()
        self.stdout = io.StringIO()
        self.stderr = io.StringIO()
        self.native = Path("winapp.exe")
        (self.project / "pyproject.toml").write_text("invalid project metadata", encoding="utf-8")
        for patcher in (
            patch("winappcli.cli.Path.cwd", return_value=self.project),
            patch("winappcli.cli.find_native_cli", return_value=self.native),
        ):
            patcher.start()
            self.addCleanup(patcher.stop)

    def forward_help(self, executable, arguments, directory):
        print(f"native help: {' '.join(arguments)}")
        return 0

    def invoke_help(self, arguments, expected_code=0):
        self.stdout.seek(0)
        self.stdout.truncate()
        with redirect_stdout(self.stdout), redirect_stderr(self.stderr):
            with self.assertRaises(SystemExit) as result:
                main(arguments)
        self.assertEqual(result.exception.code, expected_code)
        return self.stdout.getvalue()

    def test_root_help_forwards_native_then_lists_python_workflows(self):
        for flag in ("--help", "-h", "help"):
            with self.subTest(flag=flag):
                with (
                    patch("winappcli.cli.forward_native", side_effect=self.forward_help) as forward,
                    patch("winappcli.cli._prepare_codegen") as codegen,
                    patch("winappcli.cli.clear_identity") as clear,
                ):
                    text = self.invoke_help([flag])
                forward.assert_called_once_with(self.native, ["--help"], self.project)
                self.assertLess(text.index("native help:"), text.index("Python extensions:"))
                self.assertIn("winapp", text)
                for name in ("generate", "identity", "restore", "update", "run"):
                    self.assertIn(name, text)
                codegen.assert_not_called()
                clear.assert_not_called()
                self.assertFalse((self.project / ".venv").exists())

    def test_no_arguments_show_combined_help_with_existing_usage_error_code(self):
        with (
            patch("winappcli.cli.forward_native", side_effect=self.forward_help),
            redirect_stdout(self.stdout), redirect_stderr(self.stderr),
        ):
            self.assertEqual(main([]), 2)
        self.assertIn("native help:", self.stdout.getvalue())
        self.assertIn("Python extensions:", self.stdout.getvalue())

    def test_workflow_help_uses_only_the_effective_python_syntax(self):
        for command, option in (("restore", "--python"), ("update", "--channel"), ("run", "--identity")):
            with self.subTest(command=command):
                with patch("winappcli.cli.forward_native", side_effect=self.forward_help) as forward:
                    text = self.invoke_help([command, "--help"])
                if command == "run":
                    forward.assert_called_once_with(self.native, [command, "--help"], self.project)
                    self.assertLess(text.index("Native run modes"), text.index("native help:"))
                    self.assertLess(text.index("native help:"), text.index("Python run workflow:"))
                else:
                    forward.assert_not_called()
                    self.assertNotIn("native help:", text)
                    self.assertNotIn("<base-directory>", text)
                    self.assertNotIn("--setup-sdks", text)
                self.assertIn(f"winapp {command}", text)
                self.assertIn(option, text)
                self.assertIn("--project", text)
                self.assertIn("--quiet", text)
                self.assertIn("-q", text)
                self.assertIn("--verbose", text)
                self.assertIn("-v", text)

    def test_output_options_before_help_do_not_bypass_python_help(self):
        for command in ("restore", "update", "generate", "identity"):
            with (
                self.subTest(command=command),
                patch("winappcli.cli.forward_native") as forward,
                patch("winappcli.cli._prepare_codegen") as codegen,
            ):
                text = self.invoke_help(["--quiet", command, "--help"])
            self.assertIn(f"winapp {command}", text)
            self.assertIn("--quiet", text)
            forward.assert_not_called()
            codegen.assert_not_called()

    def test_native_selection_prefix_is_respected_by_help(self):
        for arguments in (
            ["--winapp", "chosen.exe", "--help"],
            ["--winapp=chosen.exe", "run", "--help"],
            ["run", "--winapp", "chosen.exe", "--help"],
            ["--quiet", "--winapp", "chosen.exe", "run", "--help"],
        ):
            with self.subTest(arguments=arguments):
                with (
                    patch("winappcli.cli.find_native_cli", return_value=self.native) as locate,
                    patch("winappcli.cli.forward_native", side_effect=self.forward_help),
                ):
                    self.invoke_help(arguments)
                self.assertEqual(locate.call_args.args[0], "chosen.exe")
                locate.assert_called_once_with("chosen.exe")

    def test_run_help_distinguishes_existing_venv_identity_and_native_inputs(self):
        with patch("winappcli.cli.forward_native", side_effect=self.forward_help):
            text = self.invoke_help(["run", "--help"])
        self.assertIn("project .venv", text)
        self.assertIn("private development environment", text)
        self.assertIn("Other inputs use the native syntax", text)
        self.assertNotIn("[--help] --identity", text)

    def test_native_help_failure_is_not_hidden_by_python_help(self):
        for command in ([], ["run"]):
            with self.subTest(command=command):
                with patch("winappcli.cli.forward_native", return_value=37):
                    text = self.invoke_help([*command, "--help"], expected_code=37)
                self.assertNotIn("Python extensions:", text)
                self.assertNotIn("Python restore workflow:", text)
                self.assertNotIn("Python run workflow:", text)

    def test_missing_native_cli_reports_an_error_instead_of_combined_success(self):
        with (
            patch("winappcli.cli.find_native_cli", side_effect=WinappError("Native CLI is missing")),
            redirect_stdout(self.stdout), redirect_stderr(self.stderr),
        ):
            self.assertEqual(main(["--help"]), 1)
        self.assertIn("Native CLI is missing", self.stderr.getvalue())
        self.assertNotIn("Python extensions:", self.stdout.getvalue())

    def test_python_only_help_does_not_need_native_cli_or_project_configuration(self):
        for arguments in (
            ["restore", "--help"], ["update", "--help"], ["generate", "--help"],
            ["identity", "--help"], ["identity", "clear", "--help"],
        ):
            with self.subTest(arguments=arguments):
                with patch("winappcli.cli.find_native_cli") as locate:
                    text = self.invoke_help(arguments)
                locate.assert_not_called()
                self.assertIn(f"winapp {' '.join(arguments[:-1])}", text)
                self.assertNotIn("native help:", text)

    def test_generic_native_help_is_forwarded_without_python_supplement(self):
        with patch("winappcli.cli.forward_native", side_effect=self.forward_help) as forward:
            with redirect_stdout(self.stdout), redirect_stderr(self.stderr):
                self.assertEqual(main(["manifest", "--help"]), 0)
        forward.assert_called_once_with(self.native, ["manifest", "--help"], self.project)
        self.assertNotIn("Python extensions:", self.stdout.getvalue())

    def test_help_after_identity_script_remains_an_application_argument(self):
        for suffix in (["--help"], ["--", "--help"]):
            with self.subTest(suffix=suffix):
                with (
                    patch("winappcli.cli._require_supported_python"),
                    patch("winappcli.cli.run_with_identity", return_value=37) as launch,
                    patch("winappcli.cli.forward_native") as forward,
                    redirect_stdout(self.stdout), redirect_stderr(self.stderr),
                ):
                    self.assertEqual(main(["run", "--identity", "app.py", *suffix]), 37)
                self.assertEqual(launch.call_args.args[2], ["--help"])
                forward.assert_not_called()

    def test_version_displays_package_and_native_versions_without_project_setup(self):
        with (
            patch("winappcli.cli.version", return_value="0.7.2.dev37") as package_version,
            patch("winappcli.cli.forward_native", side_effect=lambda *a: print("0.7.2-prerelease.37") or 0) as forward,
            patch("winappcli.cli._prepare_codegen") as codegen,
        ):
            text = self.invoke_help(["--version"])
        self.assertEqual(text.strip().splitlines(), [
            "Python Package: microsoft-winappcli v0.7.2.dev37", "Native CLI:", "0.7.2-prerelease.37",
        ])
        package_version.assert_called_once_with("microsoft-winappcli")
        forward.assert_called_once_with(self.native, ["--version"], self.project)
        codegen.assert_not_called()
        self.assertFalse((self.project / ".venv").exists())

    def test_version_respects_native_override_and_propagates_failure(self):
        with (
            patch("winappcli.cli.find_native_cli", return_value=self.native) as locate,
            patch("winappcli.cli.forward_native", return_value=37),
        ):
            self.invoke_help(["--winapp", "chosen.exe", "--version"], expected_code=37)
        locate.assert_called_once_with("chosen.exe")

    def test_missing_native_version_is_reported_as_an_error(self):
        with (
            patch("winappcli.cli.find_native_cli", side_effect=WinappError("Native CLI is missing")),
            redirect_stdout(self.stdout), redirect_stderr(self.stderr),
        ):
            self.assertEqual(main(["--version"]), 1)
        self.assertIn("Python Package: microsoft-winappcli", self.stdout.getvalue())
        self.assertIn("Native CLI is missing", self.stderr.getvalue())

    def test_completion_remains_native_forwarding(self):
        with patch("winappcli.cli.forward_native", return_value=0) as forward:
            arguments = ["complete", "--commandline", "winapp ge", "--position", "12"]
            self.assertEqual(main(arguments), 0)
        self.assertEqual(forward.call_args.args[1], arguments)

    def test_native_root_schema_preserves_output_arguments_and_exit_status(self):
        payload = '{"commands":[]}\n'
        cases = (
            (["--cli-schema"], None),
            (["--cli-schema"], "chosen.exe"),
            (["--quiet", "--cli-schema"], None),
        )
        for arguments, override in cases:
            for exit_code in (0, 37):
                with self.subTest(arguments=arguments, override=override, exit_code=exit_code):
                    self.stdout.seek(0)
                    self.stdout.truncate()
                    self.stderr.seek(0)
                    self.stderr.truncate()
                    prefix = ["--winapp", override] if override else []

                    def forward_schema(*_args):
                        print(payload, end="")
                        return exit_code

                    with (
                        patch("winappcli.cli.find_native_cli", return_value=self.native) as locate,
                        patch("winappcli.cli.forward_native", side_effect=forward_schema) as forward,
                        patch("winappcli.cli.build_parser") as parser,
                        redirect_stdout(self.stdout), redirect_stderr(self.stderr),
                    ):
                        self.assertEqual(main([*prefix, *arguments]), exit_code)

                    locate.assert_called_once_with(override)
                    forward.assert_called_once_with(self.native, arguments, self.project)
                    parser.assert_not_called()
                    self.assertEqual(self.stdout.getvalue(), payload)
                    self.assertEqual(self.stderr.getvalue(), "")
