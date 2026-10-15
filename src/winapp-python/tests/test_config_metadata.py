# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

import hashlib
from pathlib import Path
import stat
from types import SimpleNamespace
import unittest
from packaging.version import Version
from unittest.mock import patch

from winapp_cli.config import DEFAULT_PACKAGE, add_config, load_config, set_codegen_version, try_load_config
from winapp_cli.errors import WinappError
from winapp_cli.metadata import packages_hash, read_inventory, select_metadata
from winapp_cli.paths import local_path

from helpers import ProjectTest


class ConfigTests(ProjectTest):
    def test_loads_project_relative_configuration(self):
        config = self.config()
        self.assertEqual(config.package, "myapp_winrt")
        self.assertEqual(config.types, ("Windows.Foundation.Uri",))
        self.assertEqual(config.scope, "types")
        self.assertEqual(config.codegen_version, self.codegen_version)
        self.assertEqual(config.output, self.project / ".winapp" / "python" / "myapp_winrt")

    def test_deduplicates_types(self):
        self.write_config(types=["Windows.Foundation.Uri", "Windows.Foundation.Uri"])
        self.assertEqual(len(self.config().types), 1)

    def test_default_scope_does_not_require_types(self):
        self.write_config(scope="default")
        self.assertEqual(self.config().types, ())
        self.assertEqual(self.config().scope, "default")

    def test_default_scope_supports_additive_type_selection(self):
        self.write_config(scope="default", types=["Windows.Foundation.Uri"])
        self.assertEqual(self.config().types, ("Windows.Foundation.Uri",))

    def test_unknown_scope_fails_closed(self):
        self.write_config(scope="all-winui")
        with self.assertRaisesRegex(WinappError, "scope"):
            self.config()

    def test_unconfigured_project_skips_without_masking_bad_toml(self):
        (self.project / "pyproject.toml").write_text('[project]\nname = "sample"\n', encoding="utf-8")
        self.assertIsNone(try_load_config(self.project))
        with self.assertRaisesRegex(WinappError, "Missing"):
            load_config(self.project)
        (self.project / "pyproject.toml").write_text("[project\n", encoding="utf-8")
        with self.assertRaisesRegex(WinappError, "Invalid"):
            try_load_config(self.project)
        (self.project / "pyproject.toml").write_text("[tool]\nwinapp = 1\n", encoding="utf-8")
        with self.assertRaisesRegex(WinappError, "tool.winapp"):
            try_load_config(self.project)

    def test_init_adds_default_scope_without_reformatting_project(self):
        source = self.project / "pyproject.toml"
        before = b'[project]\r\nname = "Example-App"\r\n# keep this comment\r\n'
        source.write_bytes(before)
        config = add_config(self.project, "example_app_winrt")
        self.assertEqual(config.scope, "default")
        self.assertEqual(config.types, ())
        self.assertTrue(source.read_bytes().startswith(before))
        self.assertIn(b"\r\n[tool.winapp.python-bindings]\r\n", source.read_bytes())
        with self.assertRaisesRegex(WinappError, "already exists"):
            add_config(self.project, "other_winrt")
        self.assertEqual(config.package, "example_app_winrt")

    def test_init_reset_preserves_unrelated_project_settings(self):
        source = self.project / "pyproject.toml"
        source.write_text(source.read_text() + '\n[tool.other]\nkeep = "yes"\n', encoding="utf-8")
        config = add_config(self.project, "myapp_winrt", reset=True)
        self.assertEqual(config.scope, "default")
        self.assertEqual(config.types, ())
        self.assertIn('keep = "yes"', source.read_text())
        self.assertEqual(config.codegen_version, self.codegen_version)

    def test_default_package_is_short_and_does_not_depend_on_project_name(self):
        self.assertEqual(DEFAULT_PACKAGE, "winapp_bindings")
        (self.project / "pyproject.toml").write_text(
            '[project]\nname = "A-Very-Long-Project-Name"\n', encoding="utf-8",
        )
        self.assertEqual(add_config(self.project, DEFAULT_PACKAGE).package, "winapp_bindings")

    def test_package_name_validation(self):
        for name in ("../outside", "while", "dynwinrt", "winapp_cli", "sys", "json", "MyApp", "_private", "a-b"):
            with self.subTest(name=name):
                self.write_config(package=name)
                with self.assertRaises(WinappError):
                    self.config()

    def test_rejects_unknown_setting(self):
        self.write_config(extra="type = []")
        with self.assertRaisesRegex(WinappError, "Unknown"):
            self.config()

    def test_requires_explicit_type_selection(self):
        for types in ([], ["Uri"], ["Windows.Foundation.*"], [" Windows.Foundation.Uri"],
                      ["Windows.Win32.Foundation.Apis"], [False]):
            with self.subTest(types=types):
                self.write_config(types=types)
                with self.assertRaises(WinappError):
                    self.config()

    def test_configuration_does_not_require_dynwinrt_dependency_declarations(self):
        (self.project / "requirements.txt").unlink()
        (self.project / "requirements-dev.txt").unlink()
        self.assertEqual(self.config().package, "myapp_winrt")

    def test_missing_and_invalid_toml_are_actionable(self):
        source = self.project / "pyproject.toml"
        for content in ('[project]\nname = "test"', "[invalid", "[tool]\nwinapp = 1"):
            with self.subTest(content=content):
                source.write_text(content, encoding="utf-8")
                with self.assertRaises(WinappError):
                    self.config()
        source.unlink()
        with self.assertRaisesRegex(WinappError, "Missing"):
            self.config()

    def test_additional_metadata_is_project_relative(self):
        (self.project / "custom.winmd").write_bytes(b"custom metadata")
        self.write_config(extra='additional-winmds = [{winmd-path = "custom.winmd"}]')
        self.assertEqual(self.config().additional_winmds[0].path, self.project / "custom.winmd")

    def test_rejects_additional_metadata_outside_project(self):
        (self.root / "custom.winmd").write_bytes(b"custom metadata")
        self.write_config(extra='additional-winmds = [{winmd-path = "..\\\\custom.winmd"}]')
        with self.assertRaises(WinappError):
            self.config()

    def test_rejects_missing_metadata(self):
        self.write_config(extra='additional-winmds = [{winmd-path = "missing.winmd"}]')
        with self.assertRaisesRegex(WinappError, "missing"):
            self.config()

    def test_rejects_incomplete_or_invalid_additional_selectors(self):
        for entry in (
            "{}",
            '{namespace = "Windows.Storage"}',
            '{classes = ["ApplicationData"]}',
            '{namespace = "Windows.Win32.Foundation", classes = ["Apis"]}',
            '{namespace = "Windows.Storage", classes = ["Windows.Storage.ApplicationData"]}',
            '{unexpected = "value"}',
        ):
            with self.subTest(entry=entry):
                self.write_config(extra=f"additional-winmds = [{entry}]\n")
                with self.assertRaises(WinappError):
                    self.config()

    def test_missing_reference_only_metadata_is_an_error(self):
        self.write_config(extra='additional-refs = ["missing.winmd"]\n')
        with self.assertRaisesRegex(WinappError, "missing"):
            self.config()

    def test_codegen_version_is_project_configuration_not_a_dependency(self):
        selected = Version("0.1.0rc23")
        source = self.project / "pyproject.toml"
        before = self.config()
        config = set_codegen_version(self.project, selected)
        self.assertEqual(config.codegen_version, selected)
        self.assertEqual(config.types, before.types)
        self.assertEqual(config.package, before.package)
        self.assertNotIn("dynwinrt", source.read_text())
        self.assertEqual(self.config().codegen_version, selected)

    def test_codegen_version_requires_an_exact_package_version(self):
        source = self.project / "pyproject.toml"
        for value in ('"latest"', '">=0.1.0"', '"0.1.*"', '23', '""', '" 0.1.0rc23"'):
            with self.subTest(value=value):
                self.write_config()
                text = source.read_text().replace(
                    f'codegen-version = "{self.codegen_version}"', f"codegen-version = {value}",
                )
                source.write_text(text, encoding="utf-8")
                with self.assertRaisesRegex(WinappError, "exact Python package version"):
                    self.config()

    def test_initial_bindings_table_can_await_version_resolution(self):
        source = self.project / "pyproject.toml"
        source.write_text(source.read_text().replace(f'codegen-version = "{self.codegen_version}"\n', ""), encoding="utf-8")
        self.assertIsNone(self.config().codegen_version)


class InventoryTests(ProjectTest):
    def test_reads_schema_three_inventory(self):
        self.assertEqual(read_inventory(self.project), (self.winmd,))

    def test_hash_is_native_compatible(self):
        source = self.project / "winapp.yaml"
        source.write_text(
            "packages:\n  - name: 'B.Package'\n    version: '2.0.0'\n"
            '  - name: A.Package\n    version: "1.0.0" # a comment\n', encoding="utf-8",
        )
        expected = hashlib.sha256(b"a.package|1.0.0\nb.package|2.0.0").hexdigest()
        self.assertEqual(packages_hash(self.project), expected)

    def test_hash_accepts_unquoted_short_nuget_versions_and_section_case(self):
        (self.project / "winapp.yaml").write_text(
            "Packages:\n  - Name: Microsoft.WindowsAppSDK\n    Version: 2.0\n", encoding="utf-8"
        )
        self.assertEqual(
            packages_hash(self.project),
            hashlib.sha256(b"microsoft.windowsappsdk|2.0").hexdigest(),
        )

    def test_duplicate_pins_fail_before_using_a_previous_inventory(self):
        (self.project / "winapp.yaml").write_text(
            "packages:\n  - name: Microsoft.Windows.SDK.Contracts\n    version: 10.0.26100.1742\n"
            "  - name: microsoft.windows.sdk.contracts\n    version: 10.0.26100.1742\n",
            encoding="utf-8",
        )
        with self.assertRaisesRegex(WinappError, "Duplicate package"):
            read_inventory(self.project)

    def test_empty_and_invalid_package_pins_fail(self):
        source = self.project / "winapp.yaml"
        for content in ("packages: []", "packages: [", "packages:\n  - name: MissingVersion",
                        "packages:\n  - name: Test\n    version: []"):
            with self.subTest(content=content):
                source.write_text(content, encoding="utf-8")
                with self.assertRaises(WinappError):
                    packages_hash(self.project)

    def test_changed_pins_require_restore(self):
        source = self.project / "winapp.yaml"
        source.write_text(source.read_text().replace("1742", "2454"), encoding="utf-8")
        with self.assertRaisesRegex(WinappError, "restore"):
            read_inventory(self.project)

    def test_missing_hash_requires_restore(self):
        self.inventory.pop("yaml_packages_hash")
        self.save_inventory()
        with self.assertRaisesRegex(WinappError, "hash"):
            read_inventory(self.project)

    def test_wrong_schema_is_not_silently_accepted(self):
        self.inventory["schema"] = 2
        self.save_inventory()
        with self.assertRaisesRegex(WinappError, "schema"):
            read_inventory(self.project)

    def test_missing_or_invalid_inventory(self):
        source = self.project / ".winapp" / "winmds.lock.json"
        source.write_text("{invalid", encoding="utf-8")
        with self.assertRaisesRegex(WinappError, "Invalid"):
            read_inventory(self.project)
        source.unlink()
        with self.assertRaisesRegex(WinappError, "restore"):
            read_inventory(self.project)

    def test_missing_metadata_is_an_error(self):
        self.winmd.unlink()
        with self.assertRaisesRegex(WinappError, "missing"):
            read_inventory(self.project)

    def test_inventory_cannot_point_outside_cache(self):
        outside = self.project / "outside.winmd"
        outside.write_bytes(b"metadata")
        self.inventory["packages"][0]["winmds"] = [str(outside)]
        self.save_inventory()
        with self.assertRaisesRegex(WinappError, "inside"):
            read_inventory(self.project)

    def test_cache_cannot_be_a_filesystem_root(self):
        self.inventory["nuget_cache_dir"] = self.cache.anchor
        self.save_inventory()
        with self.assertRaisesRegex(WinappError, "invalid NuGet cache"):
            read_inventory(self.project)

    def test_requires_windows_sdk_metadata_not_machine_autodetection(self):
        self.inventory["packages"][0]["name"] = "Custom.Component"
        self.save_inventory()
        with self.assertRaisesRegex(WinappError, "Explicit Windows metadata"):
            read_inventory(self.project)

    def test_default_selection_matches_js_packages_and_keeps_windows_sdk_as_refs(self):
        self.add_appsdk_inventory()
        self.write_config(scope="default")
        roots, refs = select_metadata(self.config())
        self.assertEqual([path.name for path in roots],
                         ["Microsoft.Windows.AppNotifications.winmd", "Microsoft.Windows.Widgets.winmd"])
        self.assertIn(self.winmd, refs)
        self.assertIn(self.cache / "Microsoft.UI.Xaml.winmd", refs)
        self.assertIn(self.cache / "Microsoft.UI.Windowing.winmd", refs)
        self.assertNotIn(self.cache / "Microsoft.Windows.Widgets.winmd", refs)
        self.assertNotIn(self.cache / "Microsoft.Web.WebView2.winmd", [*roots, *refs])

    def test_default_selection_without_emit_inputs_is_actionable(self):
        self.write_config(scope="default")
        with self.assertRaisesRegex(WinappError, "No metadata matched"):
            select_metadata(self.config())

    def test_additional_selectors_and_reference_only_metadata_are_distinct(self):
        custom = self.project / "custom.winmd"
        custom.write_bytes(b"custom")
        refs = self.project / "dependency.winmd"
        refs.write_bytes(b"dependency")
        self.write_config(scope="default", extra=(
            'additional-winmds = [{namespace = "Windows.Storage", classes = ["ApplicationData"]}, '
            '{winmd-path = "custom.winmd", namespace = "Vendor.Custom", classes = ["Thing"]}]\n'
            'additional-refs = ["dependency.winmd"]\n'
        ))
        config = self.config()
        self.assertEqual(config.additional_winmds[0].classes, ("ApplicationData",))
        roots, references = select_metadata(config)
        self.assertEqual(roots, ())
        self.assertIn(custom, references)
        self.assertIn(refs, references)

    def test_malformed_inventory_entry_is_rejected(self):
        for value in (None, "not a list", [False], ["relative.winmd"]):
            with self.subTest(value=value):
                self.inventory["packages"][0]["winmds"] = value
                self.save_inventory()
                with self.assertRaises(WinappError):
                    read_inventory(self.project)

    def test_metadata_symlinks_are_rejected(self):
        link = self.cache / "linked.winmd"
        try:
            link.symlink_to(self.winmd)
        except OSError as error:
            self.skipTest(f"Symlink creation unavailable: {error}")
        self.inventory["packages"][0]["winmds"] = [str(link)]
        self.save_inventory()
        with self.assertRaisesRegex(WinappError, "Symlinks"):
            read_inventory(self.project)


class PathTests(ProjectTest):
    def test_rejects_network_paths(self):
        with self.assertRaisesRegex(WinappError, "network"):
            local_path(Path(r"\\server\share\metadata.winmd"))

    def test_rejects_output_escape(self):
        with self.assertRaisesRegex(WinappError, "inside"):
            local_path(self.project / ".." / "outside", boundary=self.project)

    def test_linked_parent_is_rejected_before_probing_a_descendant(self):
        parent = self.project / "references"
        leaf = parent / "metadata.winmd"
        original_lstat = Path.lstat
        checked = []

        def lstat(candidate):
            checked.append(candidate)
            if candidate == leaf:
                self.fail("A descendant was probed before rejecting its linked parent.")
            if candidate == parent:
                return SimpleNamespace(st_mode=stat.S_IFDIR, st_file_attributes=stat.FILE_ATTRIBUTE_REPARSE_POINT)
            return original_lstat(candidate)

        with patch.object(Path, "lstat", lstat):
            with self.assertRaisesRegex(WinappError, "Symlinks"):
                local_path(leaf, boundary=self.project)
        self.assertIn(parent, checked)
        self.assertNotIn(leaf, checked)

    def test_rejects_project_junction_or_symlink(self):
        link = self.root / "project-link"
        try:
            link.symlink_to(self.project, target_is_directory=True)
        except OSError as error:
            self.skipTest(f"Symlink creation unavailable: {error}")
        with self.assertRaisesRegex(WinappError, "Symlinks"):
            load_config(link)


if __name__ == "__main__":
    unittest.main()
