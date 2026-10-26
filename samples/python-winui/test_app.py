# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

from pathlib import Path
import tomllib
import unittest

from counter import Counter


class CounterTests(unittest.TestCase):
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
        runtime = (sample / "runtime.py").read_text(encoding="utf-8")
        self.assertIn("from winapp_bindings._winapp_runtime import WINDOWS_APP_SDK_VERSION, bootstrap_dll_path", runtime)
        self.assertIn("dll = bootstrap_dll_path(project)", runtime)
        self.assertNotIn("def bootstrap_dll_path", runtime)
        for name in ("app.py", "runtime.py", "window.py", "counter.py"):
            text = (sample / name).read_text(encoding="utf-8")
            self.assertNotIn("has_package_identity", text)
            self.assertNotIn("from_package_data", text)
            self.assertNotIn("XamlReader", text)


if __name__ == "__main__":
    unittest.main()
