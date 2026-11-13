# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

from contextlib import redirect_stderr, redirect_stdout
from importlib.metadata import distribution
import io
import os
from pathlib import Path
import struct
from unittest.mock import patch

from winappcli.cli import main
from winappcli.errors import WinappError
from winappcli.native import REQUIRED_NATIVE_FILES, bundled_cli, native_payload_platform, native_platform

from helpers import ProjectTest


class NativePayloadTests(ProjectTest):
    def test_installed_distribution_exports_the_expected_module_and_command(self):
        package = distribution("microsoft-winappcli")
        self.assertEqual(package.metadata["Name"], "microsoft-winappcli")
        self.assertEqual(package.read_text("top_level.txt").strip(), "winappcli")
        self.assertEqual(
            {entry.name: entry.value for entry in package.entry_points if entry.group == "console_scripts"},
            {"winapp": "winappcli.cli:main"},
        )

    def executable(self, machine=0x8664):
        path = self.project / "_native" / "winapp.exe"
        path.parent.mkdir(exist_ok=True)
        content = bytearray(90)
        content[:2] = b"MZ"
        struct.pack_into("<I", content, 60, 64)
        content[64:68] = b"PE\0\0"
        struct.pack_into("<H", content, 68, machine)
        path.write_bytes(content)
        for name in REQUIRED_NATIVE_FILES:
            if name != "winapp.exe":
                (path.parent / name).write_bytes(b"fixture")
        return path

    def test_platform_tag_uses_executable_not_build_host(self):
        for machine, tag in ((0x8664, "win_amd64"), (0xAA64, "win_arm64")):
            with self.subTest(machine=machine):
                self.assertEqual(native_platform(self.executable(machine)), tag)

    def test_invalid_or_unsupported_native_payload_fails(self):
        path = self.executable(0x014C)
        with self.assertRaisesRegex(WinappError, "unsupported Windows architecture"):
            native_platform(path)
        for content in (b"not an EXE", b"MZ" + b"\0" * 62):
            path.write_bytes(content)
            with self.assertRaises(WinappError):
                native_platform(path)

    def test_bundled_path_is_absolute_and_matches_python_platform(self):
        path = self.executable()
        with (
            patch("winappcli.native.__file__", str(self.project / "native.py")),
            patch("winappcli.native.sysconfig.get_platform", return_value="win-amd64"),
        ):
            self.assertEqual(bundled_cli(), path.resolve())

    def test_wrong_architecture_is_not_silently_used(self):
        self.executable(0xAA64)
        with (
            patch("winappcli.native.__file__", str(self.project / "native.py")),
            patch("winappcli.native.sysconfig.get_platform", return_value="win-amd64"),
            self.assertRaisesRegex(WinappError, "matching wheel"),
        ):
            bundled_cli()

    def test_missing_bundled_executable_does_not_fall_back(self):
        path = self.executable()
        path.unlink()
        with (
            patch("winappcli.native.__file__", str(self.project / "native.py")),
            self.assertRaisesRegex(WinappError, "Reinstall"),
        ):
            bundled_cli()

    def test_missing_payload_fails_for_source_and_wheel_installs_without_lookup(self):
        with (
            patch("winappcli.native.__file__", str(self.project / "native.py")),
            patch("shutil.which") as search,
            self.assertRaisesRegex(WinappError, "Native CLI files are missing"),
        ):
            bundled_cli()
        search.assert_not_called()

    def test_bundle_ignores_external_path_and_environment_override(self):
        path = self.executable()
        external = self.project / "winapp.exe"
        external.write_bytes(b"not executed")
        with (
            patch("winappcli.native.__file__", str(self.project / "native.py")),
            patch("winappcli.native.sysconfig.get_platform", return_value="win-amd64"),
            patch.dict(os.environ, {"PATH": str(self.project), "WINAPP_CLI_PATH": str(external)}),
            patch("shutil.which") as search,
        ):
            self.assertEqual(bundled_cli(), path.resolve())
        search.assert_not_called()

    def test_help_never_starts_a_project_executable_when_bundle_is_missing(self):
        external = self.project / "winapp.exe"
        external.write_bytes(b"not executed")
        errors = io.StringIO()
        with (
            patch("winappcli.native.__file__", str(self.project / "native.py")),
            patch.dict(os.environ, {"PATH": str(self.project), "WINAPP_CLI_PATH": str(external)}),
            patch("winappcli.cli.Path.cwd", return_value=self.project),
            patch("shutil.which") as search,
            patch("winappcli.cli.forward_native") as forward,
            redirect_stdout(io.StringIO()), redirect_stderr(errors),
        ):
            self.assertEqual(main(["--help"]), 1)
        search.assert_not_called()
        forward.assert_not_called()
        self.assertIn("Reinstall microsoft-winappcli", errors.getvalue())

    def test_incomplete_native_payload_is_rejected_before_install_or_execution(self):
        for name in REQUIRED_NATIVE_FILES:
            for empty in (False, True):
                with self.subTest(name=name, empty=empty):
                    path = self.executable()
                    missing = path.parent / name
                    if empty:
                        missing.write_bytes(b"")
                    else:
                        missing.unlink()
                    with self.assertRaises(WinappError) as failure:
                        native_payload_platform(path.parent)
                    self.assertIn(name, str(failure.exception))

    def test_bundled_resolution_does_not_execute_or_probe_the_cli(self):
        path = self.executable()
        with (
            patch("winappcli.native.__file__", str(self.project / "native.py")),
            patch("winappcli.native.sysconfig.get_platform", return_value="win-amd64"),
            patch("subprocess.run") as run,
        ):
            self.assertEqual(bundled_cli(), path.resolve())
        run.assert_not_called()
