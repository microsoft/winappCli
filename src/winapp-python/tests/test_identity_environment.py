# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

import os
from pathlib import Path
from unittest.mock import patch

from packaging.version import Version

from winappcli.config import set_codegen_version
from winappcli import identity_environment as environment
from winappcli.errors import WinappError
from winappcli.generate import generate
from winappcli.python_runtime import Interpreter

from helpers import ProjectTest


class IdentityEnvironmentTests(ProjectTest):
    def setUp(self):
        super().setUp()
        self.info = Interpreter("3.12.10", "x86_64")
        (self.project / "pyproject.toml").unlink()
        (self.project / "requirements.txt").write_text("", encoding="utf-8")

    def test_requirements_source_is_explicit_and_required(self):
        (self.project / "requirements.txt").unlink()
        with self.assertRaisesRegex(WinappError, "--requirements"):
            environment.dependency_inputs(self.project, None, self.info)
        other = self.project / "deps.txt"
        other.write_text("packaging==26.3\n", encoding="utf-8")
        selected, bindings, digest = environment.dependency_inputs(self.project, Path("deps.txt"), self.info)
        self.assertEqual(selected, other)
        self.assertIsNone(bindings)
        self.assertEqual(len(digest), 64)

    def test_included_requirement_changes_invalidate_inputs(self):
        (self.project / "requirements.txt").write_text("-r extra.txt\n-c constraints.txt\n", encoding="utf-8")
        extra = self.project / "extra.txt"
        extra.write_text("packaging\n", encoding="utf-8")
        (self.project / "constraints.txt").write_text("packaging==26.3\n", encoding="utf-8")
        before = environment.dependency_inputs(self.project, None, self.info)[2]
        extra.write_text("PyYAML\n", encoding="utf-8")
        self.assertNotEqual(before, environment.dependency_inputs(self.project, None, self.info)[2])

    def test_sdk_pins_participate_in_identity_rebuild_inputs(self):
        configuration = self.project / "winapp.yaml"
        configuration.write_text(
            "packages:\n  - name: Microsoft.WindowsAppSDK\n    version: 1.8.260416003\n",
            encoding="utf-8",
        )
        before = environment.dependency_inputs(self.project, None, self.info)[2]
        configuration.write_text(configuration.read_text().replace("1.8.260416003", "2.5.1"), encoding="utf-8")
        self.assertNotEqual(before, environment.dependency_inputs(self.project, None, self.info)[2])

    def test_project_metadata_changes_invalidate_inputs_without_rewriting_the_file(self):
        source = self.project / "pyproject.toml"
        source.write_bytes(b'\xef\xbb\xbf[tool.other]\r\nsetting = "original"\r\n')
        original = source.read_bytes()
        before = environment.dependency_inputs(self.project, None, self.info)[2]
        self.assertEqual(source.read_bytes(), original)
        changed = original.replace(b"original", b"changed")
        source.write_bytes(changed)
        self.assertNotEqual(before, environment.dependency_inputs(self.project, None, self.info)[2])
        self.assertEqual(source.read_bytes(), changed)

    def test_project_metadata_dates_remain_valid_fingerprint_inputs(self):
        source = self.project / "pyproject.toml"
        source.write_text('[tool.other]\ncreated = 2026-10-15T12:00:00Z\n', encoding="utf-8")
        before = environment.dependency_inputs(self.project, None, self.info)[2]
        source.write_text(source.read_text().replace("12:00", "13:00"), encoding="utf-8")
        self.assertNotEqual(before, environment.dependency_inputs(self.project, None, self.info)[2])

    def test_remote_or_outside_requirements_include_is_rejected(self):
        for line in ("-r https://example.com/requirements.txt", "-r ..\\outside.txt"):
            (self.project / "requirements.txt").write_text(line, encoding="utf-8")
            with self.subTest(line=line):
                with self.assertRaises(WinappError):
                    environment.dependency_inputs(self.project, None, self.info)

    def test_python_requirement_is_checked_before_preparation(self):
        (self.project / "pyproject.toml").write_text(
            '[project]\nrequires-python=">=3.13"\n', encoding="utf-8",
        )
        with self.assertRaisesRegex(WinappError, "selected environment uses 3.12.10"):
            environment.dependency_inputs(self.project, None, self.info)

    def test_unbuilt_bindings_are_not_silently_skipped(self):
        self.write_config()
        with self.assertRaisesRegex(WinappError, "have not been generated"):
            environment.dependency_inputs(self.project, None, self.info)

    def test_current_generated_bindings_are_installed_as_editable_input(self):
        self.write_config()
        with patch("winappcli.generate.run_tool", side_effect=self.fake_codegen):
            generated = generate(self.config(), Path("codegen.exe"), self.codegen_version)
        _, bindings, before = environment.dependency_inputs(self.project, None, self.info)
        self.assertEqual(bindings, generated)
        (self.project / "app.py").write_text("print('source edit')\n", encoding="utf-8")
        self.assertEqual(before, environment.dependency_inputs(self.project, None, self.info)[2])

    def test_project_dynwinrt_upgrade_rebuilds_private_dependencies(self):
        self.write_config()
        with patch("winappcli.generate.run_tool", side_effect=self.fake_codegen):
            generate(self.config(), Path("codegen.exe"), self.codegen_version)
            before = environment.dependency_inputs(self.project, None, self.info)[2]
            self.codegen_version = Version("0.1.0rc23")
            set_codegen_version(self.project, self.codegen_version)
            generate(self.config(), Path("codegen.exe"), self.codegen_version)
        self.assertNotEqual(before, environment.dependency_inputs(self.project, None, self.info)[2])
        self.assertIn('codegen-version = "0.1.0rc23"', (self.project / "pyproject.toml").read_text())

    def test_python_home_and_path_are_only_removed_from_child_copy(self):
        original = {"PYTHONHOME": "other", "PYTHONPATH": "other-modules",
                    "VIRTUAL_ENV": "original", "PATH": "user-path", "MYAPP_OPTION": "keep"}
        with patch.dict(os.environ, original, clear=True):
            child = environment.application_environment(self.project / "identity")
            self.assertEqual(dict(os.environ), original)
        self.assertNotIn("PYTHONHOME", child)
        self.assertNotIn("PYTHONPATH", child)
        self.assertEqual(child["MYAPP_OPTION"], "keep")
        self.assertEqual(child["VIRTUAL_ENV"], str(self.project / "identity"))
        self.assertTrue(child["PATH"].startswith(str(self.project / "identity" / "Scripts")))

    def test_requirements_and_bindings_are_resolved_together(self):
        entry = self.project / "private-python.exe"
        prefix = self.project / "identity"
        bindings = self.project / "bindings"
        with patch.object(environment, "synchronize_project") as execute:
            environment.synchronize_dependencies(self.project, entry, prefix,
                                                self.project / "requirements.txt", bindings)
        execute.assert_called_once_with(
            self.project, entry, bindings, source=self.project / "requirements.txt", private=True, verbose=False,
        )

    def test_standard_project_dependencies_do_not_need_a_second_requirements_inventory(self):
        (self.project / "requirements.txt").unlink()
        source = self.project / "pyproject.toml"
        source.write_text(
            '[project]\nname="example"\ndependencies=["packaging==26.3"]\n', encoding="utf-8",
        )
        selected, _, before = environment.dependency_inputs(self.project, None, self.info)
        self.assertEqual(selected, source)
        source.write_text(source.read_text().replace("26.3", "25.0"), encoding="utf-8")
        self.assertNotEqual(before, environment.dependency_inputs(self.project, None, self.info)[2])

    def test_uv_lockfile_participates_in_identity_rebuild_inputs(self):
        source = self.project / "pyproject.toml"
        source.write_text(
            '[project]\nname="example"\ndependencies=[]\n[tool.uv]\n', encoding="utf-8",
        )
        lock = self.project / "uv.lock"
        with self.assertRaisesRegex(WinappError, "lockfile"):
            environment.dependency_inputs(self.project, None, self.info)
        lock.write_text("version=1\n", encoding="utf-8")
        before = environment.dependency_inputs(self.project, None, self.info)[2]
        lock.write_text("version=1\n# changed\n", encoding="utf-8")
        self.assertNotEqual(before, environment.dependency_inputs(self.project, None, self.info)[2])
