# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

import json
from pathlib import Path
import tempfile
import unittest

from packaging.version import Version

from winapp_cli.config import load_config
from winapp_cli.metadata import packages_hash


class ProjectTest(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="winapp-python-test-")
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.project = self.root / "project with spaces"
        self.project.mkdir()
        self.cache = self.root / "nuget"
        self.cache.mkdir()
        self.winmd = self.cache / "Windows.Foundation.winmd"
        self.winmd.write_bytes(b"test metadata")
        self.codegen_version = Version("0.1.0rc21")
        self.write_config()
        (self.project / "winapp.yaml").write_text(
            "packages:\n  - name: Microsoft.Windows.SDK.Contracts\n"
            "    version: 10.0.26100.1742\n", encoding="utf-8",
        )
        self.inventory = {
            "schema": 3,
            "nuget_cache_dir": str(self.cache),
            "yaml_packages_hash": packages_hash(self.project),
            "packages": [{
                "name": "Microsoft.Windows.SDK.Contracts",
                "version": "10.0.26100.1742",
                "winmds": [str(self.winmd)],
            }],
        }
        self.save_inventory()

    def write_config(self, *, package="myapp_winrt", types=None, extra="", scope=None):
        types = ([] if scope == "default" else ["Windows.Foundation.Uri"]) if types is None else types
        scope_line = f'scope = "{scope or "types"}"\n'
        (self.project / "pyproject.toml").write_text(
            f'[tool.winapp.python-bindings]\npackage = "{package}"\n'
            f'{scope_line}codegen-version = "{self.codegen_version}"\ntypes = {json.dumps(types)}\n{extra}',
            encoding="utf-8",
        )
        (self.project / "requirements.txt").write_text("", encoding="utf-8")
        (self.project / "requirements-dev.txt").write_text(
            "-r requirements.txt\n", encoding="utf-8",
        )

    def save_inventory(self):
        directory = self.project / ".winapp"
        directory.mkdir(exist_ok=True)
        (directory / "winmds.lock.json").write_text(json.dumps(self.inventory), encoding="utf-8")

    def config(self):
        return load_config(self.project)

    def add_appsdk_inventory(self):
        (self.project / "winapp.yaml").write_text(
            (self.project / "winapp.yaml").read_text(encoding="utf-8")
            + "  - name: Microsoft.WindowsAppSDK\n    version: 1.8.260416003\n",
            encoding="utf-8",
        )
        for name, filename in (
            ("Microsoft.WindowsAppSDK.Foundation", "Microsoft.Windows.AppNotifications.winmd"),
            ("Microsoft.WindowsAppSDK.InteractiveExperiences", "Microsoft.UI.Windowing.winmd"),
            ("Microsoft.WindowsAppSDK.WinUI", "Microsoft.UI.Xaml.winmd"),
            ("Microsoft.WindowsAppSDK.Widgets", "Microsoft.Windows.Widgets.winmd"),
            ("Microsoft.Web.WebView2", "Microsoft.Web.WebView2.winmd"),
        ):
            file = self.cache / filename
            file.write_bytes(name.encode("utf-8"))
            self.inventory["packages"].append({
                "name": name, "version": "1.8.260416003", "winmds": [str(file)],
            })
        self.inventory["yaml_packages_hash"] = packages_hash(self.project)
        self.save_inventory()

    def fake_codegen(self, arguments, project, **kwargs):
        output = Path(arguments[arguments.index("--output") + 1])
        output.mkdir(parents=True, exist_ok=True)
        name = output.name
        (output / "__init__.py").write_text("", encoding="utf-8")
        (output / "pyproject.toml").write_text(
            f'[project]\nname = "{name.replace("_", "-")}"\nversion = "0.1.0"\n'
            f'dependencies = ["dynwinrt=={str(self.codegen_version).replace("rc", "-preview.")}"]\n',
            encoding="utf-8",
        )
        selected = (
            arguments[arguments.index("--class-name") + 1].split(",")
            if "--class-name" in arguments else ["Microsoft.Windows.AppNotifications.AppNotification"]
        )
        for type_name in selected:
            (output / f"{type_name.rsplit('.', 1)[-1]}.py").write_text(
                "# generated\n", encoding="utf-8",
            )
        return ""
