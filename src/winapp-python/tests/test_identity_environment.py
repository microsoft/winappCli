# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

import json
import os
from pathlib import Path
import sys
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
        self.entry = self.project / "private" / "Scripts" / "python.exe"
        self.entry.parent.mkdir(parents=True)
        self.entry.touch()
        self.installed = [{"metadata": {"name": "requests", "version": "2.32.5"}}]

    def inputs(self, selected=Path("requirements.txt")):
        return environment.dependency_inputs(self.project, selected, self.info)

    def project_metadata(self, text):
        (self.project / "requirements.txt").unlink(missing_ok=True)
        (self.project / "pyproject.toml").write_text(text, encoding="utf-8")

    def pip(self, command, *args, **kwargs):
        if "install" in command:
            Path(command[command.index("--report") + 1]).write_text(
                '{"version": "1", "install": []}', encoding="utf-8",
            )
        elif "inspect" in command:
            return json.dumps({"version": "1", "installed": self.installed})
        elif "freeze" in command:
            return "requests==2.32.5\n"
        elif "check" not in command:
            self.fail(f"Unexpected command: {command}")
        return ""

    def test_requirements_are_optional_and_existing_default_file_is_ignored(self):
        source = self.project / "requirements.txt"
        source.write_text("--require-hashes\n-r missing.txt\n", encoding="utf-8")
        plan = self.inputs(None)
        self.assertIsNone(plan.requirements)
        self.assertEqual(len(plan.fingerprint), 64)
        source.write_text("requests==2.32.5\n", encoding="utf-8")
        self.assertEqual(plan.fingerprint, self.inputs(None).fingerprint)

    def test_absent_dependency_source_does_not_require_an_empty_file(self):
        (self.project / "requirements.txt").unlink()
        plan = self.inputs(None)
        self.assertIsNone(plan.bindings)
        self.assertIsNone(plan.requirements)

    def test_explicit_requirements_are_used_regardless_of_other_project_sources(self):
        (self.project / "pyproject.toml").write_text(
            '[project]\ndependencies=["packaging"]\n', encoding="utf-8",
        )
        (self.project / "uv.lock").write_text("not parsed when explicitly overridden", encoding="utf-8")
        other = self.project / "deps.txt"
        other.write_text("requests==2.32.5\n", encoding="utf-8")
        plan = self.inputs(Path("deps.txt"))
        self.assertEqual(plan.requirements, other)
        self.assertNotEqual(plan.fingerprint, self.inputs(None).fingerprint)

    def test_missing_explicit_requirements_are_not_treated_as_an_empty_list(self):
        (self.project / "requirements.txt").unlink()
        with self.assertRaisesRegex(WinappError, "Requirements file does not exist"):
            self.inputs()

    def test_included_requirement_changes_invalidate_sync_inputs(self):
        (self.project / "requirements.txt").write_text("-r extra.txt\n-c constraints.txt\n", encoding="utf-8")
        extra, constraints = self.project / "extra.txt", self.project / "constraints.txt"
        extra.write_text("packaging\n", encoding="utf-8")
        constraints.write_text("packaging==26.3\n", encoding="utf-8")
        before = self.inputs().fingerprint
        extra.write_text("PyYAML\n", encoding="utf-8")
        self.assertNotEqual(before, self.inputs().fingerprint)
        before = self.inputs().fingerprint
        constraints.write_text("packaging==25.0\n", encoding="utf-8")
        self.assertNotEqual(before, self.inputs().fingerprint)

    def test_sdk_pins_do_not_reinstall_python_packages(self):
        before = self.inputs().fingerprint
        (self.project / "winapp.yaml").write_text("packages: []\n", encoding="utf-8")
        self.assertEqual(before, self.inputs().fingerprint)

    def test_project_metadata_is_not_rewritten_and_dates_are_supported(self):
        source = self.project / "pyproject.toml"
        original = b'\xef\xbb\xbf[tool.other]\r\ncreated = 2026-10-15T12:00:00Z\r\n'
        source.write_bytes(original)
        before = self.inputs().fingerprint
        self.assertEqual(source.read_bytes(), original)
        source.write_bytes(original.replace(b"12:00", b"13:00"))
        self.assertNotEqual(before, self.inputs().fingerprint)

    def test_application_metadata_is_not_a_dependency_installation_input(self):
        before = self.inputs(None).fingerprint
        for text in (
            '[project]\ndependencies=["requests>=2"]\n',
            '[build-system]\nrequires=["setuptools"]\n[project]\nname="app"\nversion="1.0"\n',
            '[project]\ndynamic=["dependencies"]\n',
            '[tool.poetry.dependencies]\nrequests="*"\n',
            '[tool.uv]\nindex-url="https://private.example/simple"\n',
            '[tool.pdm]\ndistribution=true\n',
        ):
            self.project_metadata(text)
            with (
                self.subTest(text=text),
                patch.object(environment, "installer_environment", return_value={}),
                patch.object(environment, "run_tool", side_effect=self.pip) as execute,
            ):
                plan = self.inputs(None)
                self.assertEqual(plan.fingerprint, before)
                environment.synchronize_dependencies(self.project, self.entry, plan)
                self.assertEqual([call.args[0][6] for call in execute.call_args_list], ["check", "inspect", "freeze"])
                self.assertEqual((self.project / "pyproject.toml").read_text(encoding="utf-8"), text)

    def test_lockfiles_and_manager_configuration_are_not_read_or_exported(self):
        before = self.inputs(None).fingerprint
        for name in ("uv.lock", "poetry.lock", "pdm.lock", "Pipfile", "Pipfile.lock", "pylock.toml", "uv.toml"):
            source = self.project / name
            source.write_text("not parsed by identity", encoding="utf-8")
            with self.subTest(name=name):
                self.assertEqual(self.inputs(None).fingerprint, before)
                self.assertEqual(source.read_text(encoding="utf-8"), "not parsed by identity")
        with (
            patch.object(environment, "installer_environment", return_value={}),
            patch.object(environment, "run_tool", side_effect=self.pip) as execute,
        ):
            environment.synchronize_dependencies(self.project, self.entry, self.inputs(None))
        for call in execute.call_args_list:
            self.assertEqual(call.args[0][:6], [sys.executable, "-I", "-m", "pip", "--python", str(self.entry)])
            self.assertNotIn("install", call.args[0])

    def test_remote_or_outside_requirements_include_is_rejected(self):
        for line in ("-r https://example.com/requirements.txt", "-r ..\\outside.txt"):
            (self.project / "requirements.txt").write_text(line, encoding="utf-8")
            with self.subTest(line=line), self.assertRaises(WinappError):
                self.inputs()

    def test_python_requirement_is_checked_before_preparation(self):
        self.project_metadata('[project]\nrequires-python=">=3.13"\n')
        with self.assertRaisesRegex(WinappError, "selected environment uses 3.12.10"):
            self.inputs(None)

    def test_unbuilt_bindings_are_not_silently_skipped(self):
        self.write_config()
        with self.assertRaisesRegex(WinappError, "have not been generated"):
            self.inputs()

    def test_current_generated_bindings_and_runtime_are_managed(self):
        self.write_config()
        with patch("winappcli.generate.run_tool", side_effect=self.fake_codegen):
            generated = generate(self.config(), Path("codegen.exe"), self.codegen_version)
        plan = self.inputs()
        self.assertEqual(plan.bindings, generated)
        self.assertEqual(plan.managed, ("myapp-winrt", "dynwinrt"))
        (self.project / "app.py").write_text("print('source edit')\n", encoding="utf-8")
        self.assertEqual(plan.fingerprint, self.inputs().fingerprint)

    def test_bindings_and_optional_requirements_are_installed_together_with_dependencies(self):
        self.write_config()
        with patch("winappcli.generate.run_tool", side_effect=self.fake_codegen):
            generated = generate(self.config(), Path("codegen.exe"), self.codegen_version)
        for selected in (None, Path("requirements.txt")):
            with (
                self.subTest(selected=selected),
                patch.object(environment, "installer_environment", return_value={}),
                patch.object(environment, "run_tool", side_effect=self.pip) as execute,
            ):
                environment.synchronize_dependencies(self.project, self.entry, self.inputs(selected))
            install = execute.call_args_list[0].args[0]
            self.assertEqual(install[:7], [sys.executable, "-I", "-m", "pip", "--python", str(self.entry), "install"])
            self.assertEqual(install[-2:], ["--editable", str(generated)])
            self.assertNotIn("--no-deps", install)
            self.assertEqual("-r" in install, selected is not None)
            if selected is not None:
                self.assertEqual(install[install.index("-r") + 1], str(self.project / selected))

    def test_binding_dependency_upgrade_invalidates_sync_inputs(self):
        self.write_config()
        with patch("winappcli.generate.run_tool", side_effect=self.fake_codegen):
            generate(self.config(), Path("codegen.exe"), self.codegen_version)
            before = self.inputs().fingerprint
            self.codegen_version = Version("0.1.0rc24")
            set_codegen_version(self.project, self.codegen_version)
            generate(self.config(), Path("codegen.exe"), self.codegen_version)
        self.assertNotEqual(before, self.inputs().fingerprint)

    def test_python_home_and_path_are_only_removed_from_child_copy(self):
        original = {"PYTHONHOME": "other", "PYTHONPATH": "other-modules",
                    "VIRTUAL_ENV": "original", "PATH": "user-path", "MYAPP_OPTION": "keep"}
        with patch.dict(os.environ, original, clear=True):
            child = environment.application_environment(self.project / "identity")
            self.assertEqual(dict(os.environ), original)
        self.assertNotIn("PYTHONHOME", child)
        self.assertNotIn("PYTHONPATH", child)
        self.assertEqual(child["MYAPP_OPTION"], "keep")
        self.assertTrue(child["PATH"].startswith(str(self.project / "identity" / "Scripts")))

    def test_successful_install_records_report_and_complete_snapshot(self):
        with (
            patch.object(environment, "installer_environment", return_value={}),
            patch.object(environment, "run_tool", side_effect=self.pip) as execute,
        ):
            environment.synchronize_dependencies(self.project, self.entry, self.inputs())
        self.assertIn("--report", execute.call_args_list[0].args[0])
        self.assertEqual(execute.call_args_list[1].args[0][-1], "check")
        snapshot = json.loads((self.entry.parent.parent / environment.SNAPSHOT).read_text())
        self.assertEqual(snapshot["packages"][0]["name"], "requests")
        self.assertEqual(snapshot["requirements"], "requests==2.32.5\n")
        self.assertTrue((self.entry.parent.parent / environment.REPORT).is_file())

    def test_capture_excludes_managed_packages_from_recovery_inputs(self):
        plan = environment.DependencyPlan(None, "hash", managed=("myapp-winrt", "dynwinrt"))
        with patch.object(environment, "run_tool", side_effect=self.pip) as execute:
            snapshot = environment.capture_dependencies(self.project, self.entry, plan)
        freeze = execute.call_args_list[-1].args[0]
        self.assertIn("--all", freeze)
        self.assertEqual(freeze[-4:], ["--exclude", "dynwinrt", "--exclude", "myapp-winrt"])
        self.assertEqual(snapshot["managed"], ["dynwinrt", "myapp-winrt"])

    def test_editable_recovery_uses_metadata_file_url_instead_of_windows_freeze_path(self):
        source = (self.project / "editable source").as_uri()
        self.installed.append({
            "metadata": {"name": "local-app", "version": "1.0"},
            "direct_url": {"url": source, "dir_info": {"editable": True}},
        })
        with patch.object(environment, "run_tool", side_effect=self.pip) as execute:
            snapshot = environment.capture_dependencies(self.project, self.entry, self.inputs())
        self.assertEqual(execute.call_args_list[-1].args[0][-2:], ["--exclude", "local-app"])
        self.assertEqual(snapshot["requirements"], f'requests==2.32.5\n-e "{source}"\n')

    def test_invalid_inspection_does_not_create_a_successful_snapshot(self):
        for value in (
            '{}', '{"version":"2","installed":[]}', '{"version":"1","installed":[{}]}',
            '{"version":"1","installed":[{"metadata":{"name":"app","version":"1"},"direct_url":"invalid"}]}',
        ):
            with (
                self.subTest(value=value),
                patch.object(environment, "run_tool", return_value=value),
                self.assertRaises(WinappError),
            ):
                environment.capture_dependencies(self.project, self.entry, self.inputs())
            self.assertFalse((self.entry.parent.parent / environment.SNAPSHOT).exists())

    def test_dependency_failure_does_not_publish_snapshot(self):
        with (
            patch.object(environment, "installer_environment", return_value={}),
            patch.object(environment, "run_tool", side_effect=WinappError("dependency conflict")),
            self.assertRaisesRegex(WinappError, "dependency conflict"),
        ):
            environment.synchronize_dependencies(self.project, self.entry, self.inputs())
        self.assertFalse((self.entry.parent.parent / environment.SNAPSHOT).exists())

    def test_missing_report_is_not_installation_success(self):
        with (
            patch.object(environment, "installer_environment", return_value={}),
            patch.object(environment, "run_tool", return_value=""),
            self.assertRaisesRegex(WinappError, "without writing"),
        ):
            environment.synchronize_dependencies(self.project, self.entry, self.inputs())

    def test_failed_pip_check_does_not_publish_a_successful_snapshot(self):
        def execute(command, *args, **kwargs):
            if command[-1] == "check":
                raise WinappError("installed dependency conflict")
            return self.pip(command, *args, **kwargs)

        with (
            patch.object(environment, "installer_environment", return_value={}),
            patch.object(environment, "run_tool", side_effect=execute),
            self.assertRaisesRegex(WinappError, "installed dependency conflict"),
        ):
            environment.synchronize_dependencies(self.project, self.entry, self.inputs())
        self.assertFalse((self.entry.parent.parent / environment.SNAPSHOT).exists())

    def test_recovery_checks_direct_sources_not_only_versions(self):
        snapshot = {"managed": [], "packages": [{
            "name": "local-app", "version": "1.0",
            "direct_url": {"url": (self.project / "source").as_uri(), "dir_info": {"editable": True}},
        }]}
        installed = {"managed": [], "packages": [{
            **snapshot["packages"][0], "direct_url": None,
        }]}
        with self.assertRaisesRegex(WinappError, "Could not restore.*local-app"):
            environment._validate_restored(snapshot, installed)

    def test_vcs_recovery_checks_the_commit_not_the_original_branch_spelling(self):
        source = {
            "url": "https://example.com/app.git",
            "vcs_info": {"vcs": "git", "requested_revision": "main", "commit_id": "a" * 40},
        }
        package = {"name": "local-app", "version": "1.0", "direct_url": source}
        snapshot = {"managed": [], "packages": [package]}
        restored = {**source, "vcs_info": {**source["vcs_info"], "requested_revision": "a" * 40}}
        installed = {"managed": [], "packages": [{**package, "direct_url": restored}]}
        environment._validate_restored(snapshot, installed)
        restored["vcs_info"]["commit_id"] = "b" * 40
        with self.assertRaisesRegex(WinappError, "Could not restore"):
            environment._validate_restored(snapshot, installed)

    def test_restoration_requires_the_same_user_package_versions(self):
        with patch.object(environment, "run_tool", side_effect=self.pip):
            snapshot = environment.capture_dependencies(self.project, self.entry, self.inputs())
        self.installed[0]["metadata"]["version"] = "2.0"
        with (
            patch.object(environment, "installer_environment", return_value={}),
            patch.object(environment, "run_tool", side_effect=self.pip),
            self.assertRaisesRegex(WinappError, "Could not restore.*requests"),
        ):
            environment.synchronize_dependencies(self.project, self.entry, self.inputs(), snapshot=snapshot)
        self.assertFalse((self.entry.parent.parent / ".winapp-restore.txt").exists())

    def test_recovery_refuses_sources_that_would_be_deleted_with_old_environment(self):
        snapshot = {"managed": [], "environment": str(self.entry.parent.parent), "requirements": "",
                    "packages": [{"name": "local-app", "version": "1.0", "direct_url": {
                        "url": (self.entry.parent.parent / "source").as_uri(), "dir_info": {"editable": True},
                    }}]}
        with (
            patch.object(environment, "installer_environment", return_value={}),
            patch.object(environment, "run_tool") as execute,
            self.assertRaisesRegex(WinappError, "source is inside"),
        ):
            environment.synchronize_dependencies(self.project, self.entry, self.inputs(), snapshot=snapshot)
        execute.assert_not_called()

    def test_installation_hint_targets_private_python_without_guessing_a_package_name(self):
        hint = environment.installation_hint(self.entry)
        self.assertIn(str(self.entry), hint)
        self.assertIn(str(sys.executable), hint)
        self.assertIn("install <package>", hint)

    def test_hash_enforcement_fails_without_modifying_the_input(self):
        requirements = self.project / "requirements.txt"
        requirements.write_text("--require-hashes\n", encoding="utf-8")
        before = requirements.read_bytes()
        with self.assertRaisesRegex(WinappError, "without hashes"):
            self.inputs()
        self.assertEqual(requirements.read_bytes(), before)
