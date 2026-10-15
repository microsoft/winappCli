# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

import tomllib

from winapp_cli.config import load_identity_catalog_version, record_identity_catalog_version
from winapp_cli.errors import WinappError

from helpers import ProjectTest


class IdentityConfigTests(ProjectTest):
    def test_missing_pin_is_not_replaced_by_an_implicit_default(self):
        source = self.project / "pyproject.toml"
        before = source.read_bytes()
        self.assertIsNone(load_identity_catalog_version(self.project))
        self.assertEqual(source.read_bytes(), before)
        source.unlink()
        self.assertIsNone(load_identity_catalog_version(self.project))
        self.assertFalse(source.exists())

    def test_pin_can_be_recorded_for_a_requirements_only_project(self):
        source = self.project / "pyproject.toml"
        source.unlink()
        record_identity_catalog_version(self.project, "0.13.1")
        self.assertEqual(load_identity_catalog_version(self.project), "0.13.1")
        document = tomllib.loads(source.read_text(encoding="utf-8"))
        self.assertNotIn("project", document)

    def test_automatic_pin_preserves_other_settings_comments_bom_and_line_endings(self):
        source = self.project / "pyproject.toml"
        before = (
            b'\xef\xbb\xbf# application\r\n[project]\r\nname = "example"\r\ndependencies = []\r\n'
            b'[tool.winapp.python-bindings]\r\npackage = "example_bindings"\r\n'
            b'[tool.other]\r\nkeep = true\r\n'
        )
        source.write_bytes(before)
        record_identity_catalog_version(self.project, "0.13.1")
        after = source.read_bytes()
        self.assertTrue(after.startswith(b"\xef\xbb\xbf"))
        for line in before.decode("utf-8-sig").splitlines():
            self.assertIn(line, after.decode("utf-8-sig").splitlines())
        self.assertNotIn(b"\n", after.replace(b"\r\n", b""))
        document = tomllib.loads(after.decode("utf-8-sig"))
        self.assertEqual(document["tool"]["other"], {"keep": True})
        self.assertEqual(document["tool"]["winapp"]["python-bindings"]["package"], "example_bindings")
        self.assertEqual(load_identity_catalog_version(self.project), "0.13.1")

    def test_existing_or_concurrently_added_pin_is_not_overwritten(self):
        record_identity_catalog_version(self.project, "0.13.0")
        before = (self.project / "pyproject.toml").read_bytes()
        with self.assertRaisesRegex(WinappError, "changed during selection"):
            record_identity_catalog_version(self.project, "0.13.1")
        self.assertEqual((self.project / "pyproject.toml").read_bytes(), before)

    def test_invalid_pin_unknown_setting_and_malformed_tables_are_errors(self):
        source = self.project / "pyproject.toml"
        for text in (
            '[tool.winapp.python-identity]\nuv-catalog-version = "latest"\n',
            '[tool.winapp.python-identity]\nuv-catalog-version = ""\n',
            '[tool.winapp.python-identity]\nuv-catalog-version = 13\n',
            '[tool.winapp.python-identity]\ncatalog-version = "0.13.1"\n',
            '[tool.winapp]\npython-identity = false\n',
            '[tool]\nwinapp = "other"\n',
            'tool = "other"\n',
        ):
            with self.subTest(text=text):
                source.write_text(text, encoding="utf-8")
                with self.assertRaises(WinappError):
                    load_identity_catalog_version(self.project)
                with self.assertRaises(WinappError):
                    record_identity_catalog_version(self.project, "0.13.1")
                self.assertEqual(source.read_text(encoding="utf-8"), text)
