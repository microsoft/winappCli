# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

import builtins
from contextlib import contextmanager
from pathlib import Path
from types import ModuleType
import tomllib
import unittest
from unittest.mock import Mock, patch

from counter import Counter
from runtime import windows_runtime


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

    def test_sample_uses_auto_init_without_storage_or_packaging_dependencies(self):
        sample = Path(__file__).resolve().parent
        self.assertEqual(list(sample.glob("*.xaml")), [])
        configuration = tomllib.loads((sample / "pyproject.toml").read_text(encoding="utf-8"))
        selected = [
            f"{entry['namespace']}.{name}"
            for entry in configuration["tool"]["winapp"]["python-bindings"]["additional-winmds"]
            for name in entry["classes"]
        ]
        self.assertIn("Microsoft.UI.Xaml.Controls.StackPanel", selected)
        self.assertNotIn("Microsoft.Windows.Storage.Pickers.FileOpenPicker", selected)
        self.assertNotIn("Microsoft.UI.Windowing.OverlappedPresenter", selected)
        self.assertNotIn("Windows.Storage.ApplicationData", selected)
        self.assertNotIn("dependency-groups", configuration)
        runtime = (sample / "runtime.py").read_text(encoding="utf-8")
        self.assertIn("import dynwinrt.auto_init", runtime)
        self.assertNotIn("_winapp_runtime", runtime)
        self.assertNotIn("WINAPPSDK_BOOTSTRAP_DLL_PATH", runtime)
        self.assertNotIn("def bootstrap_dll_path", runtime)
        for name in ("app.py", "runtime.py", "window.py", "counter.py"):
            text = (sample / name).read_text(encoding="utf-8")
            self.assertNotIn("from_package_data", text)
            self.assertNotIn("XamlReader", text)

    def test_sta_precedes_auto_init_and_projections_close_before_the_apartment(self):
        for identity, failure in ((False, False), (True, False), (False, True)):
            with self.subTest(identity=identity, failure=failure):
                events = []

                @contextmanager
                def scope(name):
                    events.append(f"enter {name}")
                    try:
                        yield
                    finally:
                        events.append(f"exit {name}")

                runtime = ModuleType("dynwinrt")
                runtime.RoApartment = Mock(side_effect=lambda mode: scope(f"apartment {mode}"))
                runtime.projected_lifetime_scope = lambda: scope("projections")
                runtime.has_package_identity = lambda: identity
                original_import = builtins.__import__

                def import_module(name, *args, **kwargs):
                    if name == "dynwinrt.auto_init":
                        events.append("auto init")
                        if failure:
                            raise RuntimeError("Bootstrap failed")
                        return runtime
                    return original_import(name, *args, **kwargs)

                with patch.dict("sys.modules", {"dynwinrt": runtime}), patch(
                    "builtins.__import__", side_effect=import_module,
                ):
                    if failure:
                        with self.assertRaisesRegex(RuntimeError, "Bootstrap failed"):
                            with windows_runtime():
                                self.fail("Initialization failure was suppressed")
                    else:
                        with windows_runtime() as mode:
                            expected = "Package identity" if identity else "Bootstrap"
                            self.assertEqual(mode, f"{expected} - memory only")
                            events.append("application")
                self.assertEqual(events, [
                    "enter apartment 0", "enter projections", "auto init",
                    *([] if failure else ["application"]),
                    "exit projections", "exit apartment 0",
                ])


if __name__ == "__main__":
    unittest.main()
