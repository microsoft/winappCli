# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

import json
from pathlib import Path
from unittest.mock import patch

from packaging.version import Version

from winapp_cli.config import set_codegen_version
from winapp_cli.errors import WinappError
from winapp_cli.generate import MARKER, generate, require_current_package, validate_output

from helpers import ProjectTest


class GenerationTests(ProjectTest):
    def test_one_pass_uses_all_references_and_explicit_types(self):
        def verify(arguments, project, **kwargs):
            self.assertEqual(arguments[:4], ["codegen.exe", "generate", "--lang", "py"])
            self.assertEqual(project, self.project)
            roots = Path(arguments[arguments.index("--winmd-list") + 1])
            refs = Path(arguments[arguments.index("--ref-list") + 1])
            self.assertEqual(roots.read_text(), "")
            self.assertEqual(refs.read_text().splitlines(), [str(self.winmd)])
            self.assertEqual(arguments[arguments.index("--class-name") + 1], "Windows.Foundation.Uri")
            # A random staging leaf would change generated Python imports.
            self.assertEqual(Path(arguments[arguments.index("--output") + 1]).name, "myapp_winrt")
            return self.fake_codegen(arguments, project, **kwargs)

        with patch("winapp_cli.generate.run_tool", side_effect=verify) as tool:
            output = generate(self.config(), Path("codegen.exe"), self.codegen_version)
        self.assertEqual(tool.call_count, 1)
        self.assertTrue((output / "pyproject.toml").is_file())
        self.assertTrue((output / MARKER).is_file())
        self.assertEqual(list(output.parent.iterdir()), [output])

    def test_unknown_output_is_never_overwritten(self):
        output = self.config().output
        output.mkdir(parents=True)
        note = output / "handwritten.py"
        note.write_text("keep me", encoding="utf-8")
        with patch("winapp_cli.generate.run_tool") as tool:
            with self.assertRaisesRegex(WinappError, "not a winapp-managed"):
                generate(self.config(), Path("codegen.exe"), self.codegen_version)
        tool.assert_not_called()
        self.assertEqual(note.read_text(), "keep me")

    def test_failed_codegen_preserves_previous_output(self):
        with patch("winapp_cli.generate.run_tool", side_effect=self.fake_codegen):
            output = generate(self.config(), Path("codegen.exe"), self.codegen_version)
        before = (output / "Uri.py").read_bytes()

        def fail(arguments, project, **kwargs):
            self.fake_codegen(arguments, project, **kwargs)
            raise WinappError("intentional tool failure")

        with patch("winapp_cli.generate.run_tool", side_effect=fail):
            with self.assertRaisesRegex(WinappError, "intentional"):
                generate(self.config(), Path("codegen.exe"), self.codegen_version, force=True)
        self.assertEqual((output / "Uri.py").read_bytes(), before)
        self.assertEqual(list(output.parent.iterdir()), [output])

    def test_regeneration_removes_deselected_types(self):
        with patch("winapp_cli.generate.run_tool", side_effect=self.fake_codegen):
            output = generate(self.config(), Path("codegen.exe"), self.codegen_version)
            self.write_config(types=["Windows.Storage.StorageFile"])
            generate(self.config(), Path("codegen.exe"), self.codegen_version)
        self.assertFalse((output / "Uri.py").exists())
        self.assertTrue((output / "StorageFile.py").exists())

    def test_bad_generated_runtime_pin_preserves_previous_output(self):
        with patch("winapp_cli.generate.run_tool", side_effect=self.fake_codegen):
            output = generate(self.config(), Path("codegen.exe"), self.codegen_version)

        def mismatch(arguments, project, **kwargs):
            self.fake_codegen(arguments, project, **kwargs)
            generated = Path(arguments[arguments.index("--output") + 1]) / "pyproject.toml"
            generated.write_text(
                generated.read_text().replace("0.1.0-preview.21", "0.1.0rc20"), encoding="utf-8",
            )
            return ""

        with patch("winapp_cli.generate.run_tool", side_effect=mismatch):
            with self.assertRaisesRegex(WinappError, "runtime pin"):
                generate(self.config(), Path("codegen.exe"), self.codegen_version, force=True)
        self.assertIn("0.1.0-preview.21", (output / "pyproject.toml").read_text())

    def test_missing_generated_package_is_not_success(self):
        with patch("winapp_cli.generate.run_tool", return_value=""):
            with self.assertRaisesRegex(WinappError, "installable"):
                generate(self.config(), Path("codegen.exe"), self.codegen_version)
        self.assertFalse(self.config().output.exists())

    def test_swap_failure_restores_previous_directory(self):
        with patch("winapp_cli.generate.run_tool", side_effect=self.fake_codegen):
            output = generate(self.config(), Path("codegen.exe"), self.codegen_version)
        rename = Path.rename

        def failing_rename(source, target):
            if source.name == "myapp_winrt" and source.parent.name.startswith(".generate-"):
                raise OSError("locked")
            return rename(source, target)

        with patch("winapp_cli.generate.run_tool", side_effect=self.fake_codegen):
            with patch.object(Path, "rename", failing_rename):
                with self.assertRaisesRegex(OSError, "locked"):
                    generate(self.config(), Path("codegen.exe"), self.codegen_version, force=True)
        self.assertTrue((output / "Uri.py").is_file())
        self.assertEqual(list(output.parent.iterdir()), [output])

    def test_unchanged_inputs_skip_codegen_but_changed_metadata_regenerates(self):
        with patch("winapp_cli.generate.run_tool", side_effect=self.fake_codegen) as tool:
            output = generate(self.config(), Path("codegen.exe"), self.codegen_version)
            generate(self.config(), Path("codegen.exe"), self.codegen_version)
            self.assertEqual(tool.call_count, 1)
            self.winmd.write_bytes(b"changed metadata")
            generate(self.config(), Path("codegen.exe"), self.codegen_version)
            self.assertEqual(tool.call_count, 2)
        self.assertTrue((output / "Uri.py").is_file())

    def test_modifying_a_generated_file_invalidates_the_cache(self):
        with patch("winapp_cli.generate.run_tool", side_effect=self.fake_codegen) as tool:
            output = generate(self.config(), Path("codegen.exe"), self.codegen_version)
            (output / "Uri.py").write_text("# user changed generated code", encoding="utf-8")
            generate(self.config(), Path("codegen.exe"), self.codegen_version)
        self.assertEqual(tool.call_count, 2)
        self.assertEqual((output / "Uri.py").read_text(), "# generated\n")

    def test_default_sdk_generation_passes_policy_inputs_without_type_filter(self):
        self.add_appsdk_inventory()
        self.write_config(scope="default")

        def check(arguments, project, **kwargs):
            self.assertNotIn("--class-name", arguments)
            roots = Path(arguments[arguments.index("--winmd-list") + 1]).read_text()
            refs = Path(arguments[arguments.index("--ref-list") + 1]).read_text()
            self.assertIn("AppNotifications.winmd", roots)
            self.assertIn("Widgets.winmd", roots)
            self.assertNotIn("Microsoft.UI.Xaml.winmd", roots)
            self.assertIn("Microsoft.UI.Xaml.winmd", refs)
            self.assertNotIn("WebView2.winmd", roots + refs)
            return self.fake_codegen(arguments, project, **kwargs)

        with patch("winapp_cli.generate.run_tool", side_effect=check) as tool:
            output = generate(self.config(), Path("codegen.exe"), self.codegen_version)
            generate(self.config(), Path("codegen.exe"), self.codegen_version)
        self.assertEqual(tool.call_count, 1)
        self.assertTrue((output / "AppNotification.py").is_file())

    def test_marker_for_another_package_is_rejected(self):
        output = self.config().output
        output.mkdir(parents=True)
        (output / MARKER).write_text('{"schema": 1, "package": "someone_else"}', encoding="utf-8")
        with self.assertRaisesRegex(WinappError, "Unrecognized"):
            validate_output(output, "myapp_winrt")

    def test_default_output_and_extra_type_passes_are_combined_before_publication(self):
        self.add_appsdk_inventory()
        self.write_config(scope="default", types=["Windows.Foundation.Uri"], extra=(
            'additional-winmds = [{namespace = "Windows.Storage", classes = ["ApplicationData"]}]\n'
        ))
        with patch("winapp_cli.generate.run_tool", side_effect=self.fake_codegen) as tool:
            output = generate(self.config(), Path("codegen.exe"), self.codegen_version)
        self.assertEqual(tool.call_count, 3)
        self.assertTrue((output / "AppNotification.py").exists())
        self.assertTrue((output / "Uri.py").exists())
        self.assertTrue((output / "ApplicationData.py").exists())
        self.assertEqual(tool.call_args_list[-1].args[0][-4:],
                         ["--namespace", "Windows.Storage", "--class-name", "ApplicationData"])

    def test_file_specific_selection_keeps_other_files_as_references(self):
        custom = self.project / "custom.winmd"
        custom.write_bytes(b"custom")
        reference = self.project / "reference.winmd"
        reference.write_bytes(b"reference")
        self.write_config(scope="types", types=[], extra=(
            'additional-winmds = [{winmd-path = "custom.winmd", namespace = "Vendor.Custom", classes = ["Thing"]}]\n'
            'additional-refs = ["reference.winmd"]\n'
        ))

        def verify(arguments, project, **kwargs):
            roots = Path(arguments[arguments.index("--winmd-list") + 1]).read_text().splitlines()
            refs = Path(arguments[arguments.index("--ref-list") + 1]).read_text().splitlines()
            self.assertEqual(roots, [str(custom)])
            self.assertIn(str(reference), refs)
            self.assertIn(str(self.winmd), refs)
            self.assertNotIn(str(custom), refs)
            return self.fake_codegen(arguments, project, **kwargs)

        with patch("winapp_cli.generate.run_tool", side_effect=verify):
            generate(self.config(), Path("codegen.exe"), self.codegen_version)

    def test_extra_selector_change_invalidates_generation_without_metadata_change(self):
        self.add_appsdk_inventory()
        self.write_config(scope="default", extra=(
            'additional-winmds = [{namespace = "Windows.Foundation", classes = ["Uri"]}]\n'
        ))
        with patch("winapp_cli.generate.run_tool", side_effect=self.fake_codegen) as tool:
            output = generate(self.config(), Path("codegen.exe"), self.codegen_version)
            before = tool.call_count
            self.write_config(scope="default", extra=(
                'additional-winmds = [{namespace = "Windows.Storage", classes = ["ApplicationData"]}]\n'
            ))
            generate(self.config(), Path("codegen.exe"), self.codegen_version)
        self.assertEqual(tool.call_count, before + 2)
        self.assertFalse((output / "Uri.py").exists())
        self.assertTrue((output / "ApplicationData.py").exists())

    def test_project_codegen_version_update_regenerates_matching_runtime(self):
        source = self.project / "pyproject.toml"
        with patch("winapp_cli.generate.run_tool", side_effect=self.fake_codegen) as tool:
            output = generate(self.config(), Path("codegen.exe"), self.codegen_version)
            self.codegen_version = Version("0.1.0rc23")
            set_codegen_version(self.project, self.codegen_version)
            generate(self.config(), Path("codegen.exe"), self.codegen_version)
            self.assertEqual(tool.call_count, 2)
            generate(self.config(), Path("codegen.exe"), self.codegen_version)
            self.assertEqual(tool.call_count, 2)
        marker = json.loads((output / MARKER).read_text())
        self.assertEqual(marker["codegen_version"], "0.1.0rc23")
        self.assertIn("dynwinrt==0.1.0-preview.23", (output / "pyproject.toml").read_text())
        self.assertIn('codegen-version = "0.1.0rc23"', source.read_text())
        self.assertNotIn("dependencies", source.read_text())
        self.assertEqual(require_current_package(self.config()), output)

    def test_generated_version_is_not_limited_to_the_winapp_release(self):
        self.codegen_version = Version("0.2.0")
        set_codegen_version(self.project, self.codegen_version)
        with patch("winapp_cli.generate.run_tool", side_effect=self.fake_codegen):
            output = generate(self.config(), Path("codegen.exe"), self.codegen_version)
        self.assertEqual(require_current_package(self.config()), output)

    def test_project_version_change_requires_regeneration_before_identity_reuse(self):
        with patch("winapp_cli.generate.run_tool", side_effect=self.fake_codegen):
            output = generate(self.config(), Path("codegen.exe"), self.codegen_version)
        before = (output / "pyproject.toml").read_bytes()
        set_codegen_version(self.project, Version("0.1.0rc23"))
        with self.assertRaisesRegex(WinappError, "project's codegen-version"):
            require_current_package(self.config())
        with (
            patch("winapp_cli.generate.run_tool") as tool,
            self.assertRaisesRegex(WinappError, "Codegen does not match"),
        ):
            generate(self.config(), Path("codegen.exe"), self.codegen_version)
        tool.assert_not_called()
        self.assertEqual((output / "pyproject.toml").read_bytes(), before)

    def test_missing_codegen_marker_version_cannot_reuse_the_cache(self):
        with patch("winapp_cli.generate.run_tool", side_effect=self.fake_codegen) as tool:
            output = generate(self.config(), Path("codegen.exe"), self.codegen_version)
            marker = output / MARKER
            state = json.loads(marker.read_text())
            del state["codegen_version"]
            marker.write_text(json.dumps(state), encoding="utf-8")
            generate(self.config(), Path("codegen.exe"), self.codegen_version)
        self.assertEqual(tool.call_count, 2)

    def test_identity_uses_generated_version_without_looking_up_latest(self):
        with patch("winapp_cli.generate.run_tool", side_effect=self.fake_codegen):
            output = generate(self.config(), Path("codegen.exe"), self.codegen_version)
        with patch("winapp_cli.generate.run_tool") as tool:
            self.assertEqual(require_current_package(self.config()), output)
        tool.assert_not_called()

    def test_invalid_generated_version_is_an_explicit_identity_error(self):
        with patch("winapp_cli.generate.run_tool", side_effect=self.fake_codegen):
            output = generate(self.config(), Path("codegen.exe"), self.codegen_version)
        marker = output / MARKER
        original = json.loads(marker.read_text())
        for value in (None, 23, "latest"):
            with self.subTest(value=value):
                marker.write_text(json.dumps({**original, "codegen_version": value}), encoding="utf-8")
                with self.assertRaisesRegex(WinappError, "Invalid generated"):
                    require_current_package(self.config())
