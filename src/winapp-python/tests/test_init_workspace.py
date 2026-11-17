# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

from contextlib import redirect_stderr
import io
import json
from pathlib import Path
from unittest.mock import patch

from winappcli.init_workspace import find_init_workspaces, resolve_init_workspace

from helpers import ProjectTest


class InitWorkspaceTests(ProjectTest):
    def resolve(self, *arguments, noninteractive=False):
        return resolve_init_workspace(["init", *arguments], self.project, noninteractive=noninteractive)

    def workspace(self, name):
        project = self.project / name
        project.mkdir(parents=True)
        (project / "winapp.yaml").write_bytes((self.project / "winapp.yaml").read_bytes())
        (project / ".winapp").mkdir()
        (project / ".winapp" / "winmds.lock.json").write_text(json.dumps(self.inventory), encoding="utf-8")
        return project

    def test_explicit_directory_and_defaults_resolve_without_a_native_report(self):
        self.assertEqual(self.resolve(".").project, self.project)
        self.assertEqual(self.resolve("--use-defaults").project, self.project)
        self.assertEqual(self.resolve(noninteractive=True).project, self.project)
        self.assertIsNone(self.resolve().project)
        self.assertIsNone(self.resolve("--use-defaults=false").project)

    def test_value_options_and_boolean_values_are_not_project_directories(self):
        arguments = [
            "--caller", "python", "--project-framework", "winui",
            "--setup-sdks=preview", "--config-dir", str(self.project),
            "--quiet", "false", "--verbose", "true", "child with spaces",
        ]
        resolved = self.resolve(*arguments)
        self.assertEqual(resolved.project, self.project / "child with spaces")
        self.assertEqual(resolved.config_directory, self.project)
        self.assertFalse(resolved.skip_bindings)

    def test_aliases_repeated_options_and_terminator_keep_native_meaning(self):
        self.assertIsNone(self.resolve("--use-defaults", "--no-prompt=false").project)
        self.assertEqual(self.resolve("--no-prompt=false", "--use-defaults").project, self.project)
        self.assertEqual(self.resolve("--use-defaults:true").project, self.project)
        self.assertEqual(self.resolve("--", "-directory").project, self.project / "-directory")
        self.assertFalse(self.resolve("--ignore-config", "--no-config=false").skip_bindings)
        self.assertTrue(self.resolve("--config-only").config_only)

    def test_unknown_options_do_not_create_a_guessed_target(self):
        self.assertIsNone(self.resolve("--future-option", "value", "--use-defaults").project)

    def test_skip_modes_do_not_use_old_metadata_to_enable_bindings(self):
        for arguments in (["--sparse"], ["--setup-sdks", "none"], ["--ignore-config"]):
            with self.subTest(arguments=arguments):
                self.assertTrue(self.resolve(*arguments).skip_bindings)

    def test_discovery_includes_unchanged_ready_projects_without_selecting_one(self):
        child = self.workspace("child")
        self.assertEqual(find_init_workspaces(self.project, config_only=False), [self.project, child])

    def test_config_only_can_find_valid_configuration_without_a_new_lockfile(self):
        (self.project / ".winapp" / "winmds.lock.json").unlink()
        self.assertEqual(find_init_workspaces(self.project, config_only=True), [self.project])
        self.assertEqual(find_init_workspaces(self.project, config_only=False), [])

    def test_discovery_excludes_generated_output_dependencies_and_virtual_environments(self):
        for name in (".winapp\\generated", ".venv", "node_modules", "artifacts", "bin"):
            self.workspace(name)
        environment = self.workspace("custom-environment")
        (environment / "pyvenv.cfg").write_text("home = Python", encoding="utf-8")
        self.assertEqual(find_init_workspaces(self.project, config_only=False), [self.project])

    def test_bad_inventory_is_reported_without_hiding_other_candidates(self):
        child = self.workspace("valid")
        (self.project / ".winapp" / "winmds.lock.json").write_text('{"schema":1}', encoding="utf-8")
        output = io.StringIO()
        with redirect_stderr(output):
            self.assertEqual(find_init_workspaces(self.project, config_only=False), [child])
        self.assertIn("Skipping bindings candidate", output.getvalue())
        self.assertIn("schema", output.getvalue())

    def test_unreadable_directory_is_reported(self):
        output = io.StringIO()
        with (
            patch("winappcli.init_workspace.os.scandir", side_effect=PermissionError("denied")),
            redirect_stderr(output),
        ):
            self.assertEqual(find_init_workspaces(self.project, config_only=False), [self.project])
        self.assertIn("Cannot search", output.getvalue())
