# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

import json
from pathlib import Path
import tempfile
import tomllib
import unittest

from counter import Counter
from runtime import bootstrap_dll_path, windows_appsdk_version


class CounterTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)

    def test_counter_starts_at_zero_and_increments(self):
        counter = Counter()
        self.assertEqual(counter.value, 0)
        counter.increment()
        counter.increment()
        self.assertEqual(counter.value, 2)

    def test_counter_decrements_and_stops_at_zero(self):
        counter = Counter(value=2)
        counter.decrement()
        self.assertEqual(counter.value, 1)
        counter.decrement()
        counter.decrement()
        self.assertEqual(counter.value, 0)

    def test_new_counter_forgets_previous_state(self):
        previous = Counter()
        previous.increment()
        self.assertEqual(Counter().value, 0)
        self.assertFalse(hasattr(previous, "save_path"))

    def test_large_values_are_preserved_when_stepping(self):
        counter = Counter(value=9999999999)
        counter.increment()
        self.assertEqual(counter.value, 10000000000)
        counter.decrement()
        self.assertEqual(counter.value, 9999999999)

    def test_bootstrap_dll_uses_python_architecture(self):
        for platform, architecture in (("win-amd64", "x64"), ("win-arm64", "arm64")):
            with self.subTest(platform=platform):
                dll = self.root / ".winapp" / "bin" / architecture / "Microsoft.WindowsAppRuntime.Bootstrap.dll"
                dll.parent.mkdir(parents=True)
                dll.touch()
                self.assertEqual(bootstrap_dll_path(self.root, platform), dll)
                dll.unlink()
                with self.assertRaises(FileNotFoundError):
                    bootstrap_dll_path(self.root, platform)
        with self.assertRaises(RuntimeError):
            bootstrap_dll_path(self.root, "win32")

    def test_windows_appsdk_version_uses_the_product_package(self):
        (self.root / ".winapp").mkdir()
        inventory = {"schema": 3, "packages": [
            {"name": "Microsoft.WindowsAppSDK", "version": "1.8.260416003"},
            {"name": "Microsoft.WindowsAppSDK.Foundation", "version": "1.7.1"},
        ]}
        (self.root / ".winapp" / "winmds.lock.json").write_text(json.dumps(inventory), encoding="utf-8")
        self.assertEqual(windows_appsdk_version(self.root), (1, 8))

    def test_windows_appsdk_version_rejects_invalid_inventory(self):
        (self.root / ".winapp").mkdir()
        package = {"name": "Microsoft.WindowsAppSDK", "version": "2.5.1"}
        for inventory in (
            [], {"schema": 2, "packages": [package]}, {"schema": 3}, {"schema": 3, "packages": []},
            {"schema": 3, "packages": [package, package]},
            {"schema": 3, "packages": [{"name": "Microsoft.WindowsAppSDK", "version": "2.5.1-preview1"}]},
        ):
            with self.subTest(inventory=inventory):
                (self.root / ".winapp" / "winmds.lock.json").write_text(json.dumps(inventory), encoding="utf-8")
                with self.assertRaises(ValueError):
                    windows_appsdk_version(self.root)

    def test_sample_is_bootstrap_only_without_identity_storage_or_packaging_dependencies(self):
        sample = Path(__file__).resolve().parent
        self.assertEqual(list(sample.glob("*.xaml")), [])
        configuration = tomllib.loads((sample / "pyproject.toml").read_text(encoding="utf-8"))
        selected = configuration["tool"]["winapp"]["python-bindings"]["types"]
        self.assertIn("Microsoft.UI.Xaml.Controls.StackPanel", selected)
        self.assertNotIn("Microsoft.Windows.Storage.Pickers.FileOpenPicker", selected)
        self.assertNotIn("Microsoft.UI.Windowing.OverlappedPresenter", selected)
        self.assertNotIn("Windows.Storage.ApplicationData", selected)
        self.assertNotIn("dependency-groups", configuration)
        for name in ("app.py", "runtime.py", "window.py", "counter.py"):
            text = (sample / name).read_text(encoding="utf-8")
            self.assertNotIn("has_package_identity", text)
            self.assertNotIn("from_package_data", text)
            self.assertNotIn("XamlReader", text)


if __name__ == "__main__":
    unittest.main()
