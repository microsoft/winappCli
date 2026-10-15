# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

from pathlib import Path
import struct
from unittest.mock import Mock, patch

from winapp_cli.errors import WinappError
from winapp_cli.native import bundled_cli, native_platform

from helpers import ProjectTest


class NativePayloadTests(ProjectTest):
    def executable(self, machine=0x8664):
        path = self.project / "_native" / "winapp.exe"
        path.parent.mkdir(exist_ok=True)
        content = bytearray(90)
        content[:2] = b"MZ"
        struct.pack_into("<I", content, 60, 64)
        content[64:68] = b"PE\0\0"
        struct.pack_into("<H", content, 68, machine)
        path.write_bytes(content)
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
            patch("winapp_cli.native.__file__", str(self.project / "native.py")),
            patch("winapp_cli.native.sysconfig.get_platform", return_value="win-amd64"),
        ):
            self.assertEqual(bundled_cli(), path.resolve())

    def test_wrong_architecture_is_not_silently_used(self):
        self.executable(0xAA64)
        with (
            patch("winapp_cli.native.__file__", str(self.project / "native.py")),
            patch("winapp_cli.native.sysconfig.get_platform", return_value="win-amd64"),
            self.assertRaisesRegex(WinappError, "matching wheel"),
        ):
            bundled_cli()

    def test_missing_bundled_executable_does_not_fall_back(self):
        path = self.executable()
        path.unlink()
        with (
            patch("winapp_cli.native.__file__", str(self.project / "native.py")),
            self.assertRaisesRegex(WinappError, "reinstall"),
        ):
            bundled_cli()

    def test_deleted_payload_directory_is_detected_from_wheel_metadata(self):
        metadata = Mock()
        metadata.read_text.return_value = "Tag: py3-none-win_amd64\n"
        with (
            patch("winapp_cli.native.__file__", str(self.project / "native.py")),
            patch("winapp_cli.native.distribution", return_value=metadata),
            self.assertRaisesRegex(WinappError, "Bundled native CLI is missing"),
        ):
            bundled_cli()

    def test_editable_source_can_use_external_cli(self):
        metadata = Mock()
        metadata.read_text.return_value = "Tag: py3-none-any\n"
        with (
            patch("winapp_cli.native.__file__", str(self.project / "native.py")),
            patch("winapp_cli.native.distribution", return_value=metadata),
        ):
            self.assertIsNone(bundled_cli())
