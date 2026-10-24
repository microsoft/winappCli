# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

import ast
from contextlib import nullcontext, redirect_stdout
import importlib.util
import io
from pathlib import Path
import re
from types import ModuleType, SimpleNamespace
import unittest
from unittest.mock import Mock, patch


class IdentitySampleTests(unittest.TestCase):
    def load_app(self, identified):
        runtime = ModuleType("dynwinrt")
        runtime.RoApartment = Mock(return_value=nullcontext())
        runtime.projected_lifetime_scope = Mock(return_value=nullcontext())
        runtime.has_package_identity = Mock(return_value=identified)
        application_model = ModuleType("winapp_bindings.windows.application_model")
        self.current = Mock(return_value=SimpleNamespace(id=SimpleNamespace(family_name="Example_hash")))
        application_model.Package = SimpleNamespace(get_current=self.current)
        spec = importlib.util.spec_from_file_location("identity_sample", Path(__file__).with_name("app.py"))
        module = importlib.util.module_from_spec(spec)
        with patch.dict("sys.modules", {
            "dynwinrt": runtime,
            "winapp_bindings.windows.application_model": application_model,
        }):
            spec.loader.exec_module(module)
        return module

    def test_ordinary_execution_does_not_call_the_identity_gated_api(self):
        app = self.load_app(False)
        output = io.StringIO()
        with redirect_stdout(output):
            self.assertEqual(app.main(), 0)
        self.assertEqual(output.getvalue(), "Not packaged\n")
        self.current.assert_not_called()

    def test_identified_execution_reads_the_projected_family_name(self):
        app = self.load_app(True)
        output = io.StringIO()
        with redirect_stdout(output):
            self.assertEqual(app.main(), 0)
        self.assertEqual(output.getvalue(), "Package Family Name: Example_hash\n")
        self.current.assert_called_once()

    def test_other_api_failures_are_not_reported_as_not_packaged(self):
        app = self.load_app(True)
        self.current.side_effect = OSError("unexpected API failure")
        with self.assertRaisesRegex(OSError, "unexpected API failure"):
            app.main()

    def test_guide_code_is_the_same_application_as_the_sample(self):
        sample = Path(__file__).resolve().parent
        guide = (sample.parent.parent / "docs" / "guides" / "python.md").read_text(encoding="utf-8")
        blocks = re.findall(r"```python\s*\n(.*?)```", guide, flags=re.DOTALL)
        self.assertEqual(len(blocks), 1)
        self.assertEqual(
            ast.dump(ast.parse(blocks[0])),
            ast.dump(ast.parse((sample / "app.py").read_text(encoding="utf-8"))),
        )


if __name__ == "__main__":
    unittest.main()
