# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

from contextlib import redirect_stderr, redirect_stdout
import io
import json
from pathlib import Path
import shutil
import subprocess
import sys
import time
from unittest.mock import patch

from winappcli import identity
from winappcli import python_runtime as runtime
from winappcli.errors import WinappError
from winappcli.generate import generate
from winappcli.identity_environment import dependency_inputs
from winappcli.python_runtime import Distribution, Interpreter

from helpers import ProjectTest


class PythonIdentityTests(ProjectTest):
    def setUp(self):
        super().setUp()
        (self.project / "pyproject.toml").unlink()
        (self.project / "requirements.txt").write_text("", encoding="utf-8")
        (self.project / "Package.appxmanifest").write_text(
            '<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">'
            '<Identity Name="LocalPythonProbe" Publisher="CN=LocalPythonProbe" Version="1.0.0.0" />'
            '<Applications><Application Id="App" Executable="$targetnametoken$.exe" /></Applications>'
            '</Package>', encoding="utf-8",
        )
        (self.project / "app.py").write_text("print('hello')\n", encoding="utf-8")
        self.info = Interpreter("3.12.10", "x86_64")
        self.distribution = Distribution(
            self.info, "20250529",
            "https://github.com/astral-sh/python-build-standalone/releases/download/20250529/"
            "cpython-3.12.10%2B20250529-x86_64-pc-windows-msvc-install_only_stripped.tar.gz",
            "a" * 64,
        )
        for patcher in (
            patch.object(identity, "select_identity_python", return_value=Path(sys.executable)),
            patch.object(identity, "probe_interpreter", return_value=self.info),
            patch.object(identity, "capture_dependencies", return_value={
                "schema": 1, "managed": [], "packages": [], "requirements": "", "environment": "",
            }),
            patch.object(identity, "_require_developer_mode"),
            patch.object(identity.Path, "home", return_value=self.root),
        ):
            patcher.start()
            self.addCleanup(patcher.stop)

    def prepare_files(self, project, root, distribution, **kwargs):
        venv = root / kwargs.get("environment_name", "venv")
        runtime = venv / "runtime"
        runtime.mkdir(parents=True)
        (runtime / "python.exe").write_bytes(b"private PBS exe")
        (runtime / "python312.dll").write_bytes(b"private PBS dll")
        (venv / "Scripts").mkdir()
        (venv / "Scripts" / "python.exe").write_bytes(b"private launcher")
        (venv / "pyvenv.cfg").write_text(f"home = {runtime}\n", encoding="utf-8")
        return venv, runtime, venv / "Scripts" / "python.exe"

    def record(self, requirements=Path("requirements.txt")):
        root = self.project / ".winapp" / identity.ROOT_NAME
        root.mkdir()
        owner = "a" * 32
        (root / identity.PRIVATE_MARKER).write_text(json.dumps({
            "schema": 2, "project": str(self.project), "owner": owner,
        }), encoding="utf-8")
        venv, runtime, entry = self.prepare_files(self.project, root, self.distribution)
        record = {
            "schema": 2, "project": str(self.project), "root": str(root), "owner": owner,
            "state": "ready", "entry": str(entry), "base": str(runtime / "python.exe"),
            "package": "LocalPythonProbe.debug", "publisher": "CN=LocalPythonProbe",
            "full_name": "LocalPythonProbe.debug_1.0.0.0_x64__abc", "registration_attempted": True,
            "interpreter": self.info.document(), "distribution": self.distribution.document(),
            "manifest_hash": identity._registration_digest(self.project, self.project / "Package.appxmanifest"),
            "dependency_hash": dependency_inputs(self.project, requirements, self.info).fingerprint,
            "runtime_hashes": {},
        }
        record["runtime_hashes"] = identity._runtime_hashes(record)
        location = self.project / ".winapp" / "debug"
        location.mkdir(exist_ok=True)
        shutil.copyfile(self.project / "Package.appxmanifest", location / "AppxManifest.xml")
        identity._write_record(self.project, record)
        return record

    def package(self, record=None):
        return {
            "Name": record["package"] if record else "LocalPythonProbe.debug",
            "Publisher": record["publisher"] if record else "CN=LocalPythonProbe",
            "PackageFullName": record["full_name"] if record else "LocalPythonProbe.debug_1.0.0.0_x64__abc",
            "InstallLocation": str(self.project / ".winapp" / "debug"),
            "IsDevelopmentMode": True,
        }

    def run_app(self, *args, requirements=Path("requirements.txt")):
        return identity.run_with_identity(self.project, Path("app.py"), list(args),
                                         Path("winapp.exe"), None, requirements_option=requirements)

    def test_missing_or_hash_enforcing_requirements_fail_before_runtime_or_registration(self):
        for selected in (Path("missing.txt"), Path("requirements.txt")):
            (self.project / "requirements.txt").write_text("--require-hashes\n", encoding="utf-8")
            with (
                self.subTest(selected=selected),
                patch.object(identity, "resolve_distribution") as resolve,
                patch.object(identity, "prepare_runtime") as prepare,
                patch.object(identity, "_packages") as packages,
                patch.object(identity, "run_tool") as register,
                self.assertRaisesRegex(WinappError, "does not exist|without hashes"),
            ):
                identity.run_with_identity(
                    self.project, Path("app.py"), [], Path("winapp.exe"), None, requirements_option=selected,
                )
            resolve.assert_not_called()
            prepare.assert_not_called()
            packages.assert_not_called()
            register.assert_not_called()
            self.assertFalse((self.project / ".winapp" / identity.ROOT_NAME).exists())
            self.assertFalse((self.project / ".winapp" / identity.RECORD_NAME).exists())

    def assert_update(self, record, *, info=None, distribution=None):
        info = self.info if info is None else info
        distribution = distribution or Distribution(
            info, "20250529",
            "https://github.com/astral-sh/python-build-standalone/releases/download/20250529/"
            f"cpython-{info.version}%2B20250529-{info.architecture}-pc-windows-msvc-install_only_stripped.tar.gz",
            "a" * 64,
        )
        name, publisher = identity._manifest_identity(self.project / "Package.appxmanifest")
        package = {
            **self.package(), "Name": name, "Publisher": publisher,
            "PackageFullName": f"{name}_1.0.0.0_x64__abc",
        }
        registrations = {record["package"]: [self.package(record)]}
        changed_python = record["interpreter"] != info.document()
        changed_manifest = record["manifest_hash"] != identity._registration_digest(
            self.project, self.project / "Package.appxmanifest",
        )
        changed_dependencies = record["dependency_hash"] != dependency_inputs(
            self.project, Path("requirements.txt"), info,
        ).fingerprint

        def remove(script, **kwargs):
            self.assertIn("-PreserveApplicationData", script)
            registrations[record["package"]] = []
            return ""

        def register(arguments, *args, **kwargs):
            registrations[name] = [package]
            location = Path(package["InstallLocation"])
            location.mkdir(exist_ok=True)
            shutil.copyfile(self.project / "Package.appxmanifest", location / "AppxManifest.xml")
            with Path(arguments[2]).open("ab") as stream:
                stream.write(b"identity manifest")
            return ""

        output = io.StringIO()
        with (
            patch.object(identity, "probe_interpreter", return_value=info),
            patch.object(identity, "_packages", side_effect=lambda key: registrations.get(key, [])),
            patch.object(identity, "resolve_distribution", return_value=distribution) as resolve,
            patch.object(identity, "prepare_runtime", side_effect=self.prepare_files) as prepare,
            patch.object(identity, "synchronize_dependencies") as install,
            patch.object(identity, "_powershell", side_effect=remove) as remove_call,
            patch.object(identity, "run_tool", side_effect=register) as native,
            patch.object(identity, "_verify_interpreter") as verify,
            patch.object(identity.subprocess, "run", return_value=subprocess.CompletedProcess([], 0)),
            redirect_stderr(output),
        ):
            self.assertEqual(self.run_app(), 0)
        if record["interpreter"] == info.document():
            resolve.assert_not_called()
        else:
            resolve.assert_called_once_with(info)
        self.assertEqual(prepare.call_count, int(changed_python))
        self.assertEqual(install.call_count, int(changed_python or changed_dependencies))
        self.assertEqual(native.call_count, int(changed_python or changed_manifest))
        self.assertEqual(remove_call.call_count, int(changed_python or changed_manifest))
        self.assertGreaterEqual(verify.call_count, 1)
        self.assertIn("Rebuilding" if changed_python else "Updating", output.getvalue())
        current = identity._load_record(self.project)
        self.assertEqual(current["owner"], record["owner"])
        if changed_python:
            self.assertNotEqual(current["entry"], record["entry"])
            self.assertFalse(Path(record["entry"]).exists())
        else:
            self.assertEqual(current["entry"], record["entry"])
        self.assertEqual(current["interpreter"], info.document())
        self.assertEqual(current["state"], "ready")
        self.assertEqual(current["package"], name)
        self.assertEqual(current["publisher"], publisher)
        self.assertEqual(current["runtime_hashes"], identity._runtime_hashes(current))
        return current

    def test_new_identity_prepares_matching_runtime_dependencies_and_native_registration(self):
        with (
            patch.object(identity, "_packages", side_effect=[[], [], [self.package()]]),
            patch.object(identity, "resolve_distribution", return_value=self.distribution),
            patch.object(identity, "prepare_runtime", side_effect=self.prepare_files),
            patch.object(identity, "synchronize_dependencies") as install,
            patch.object(identity, "run_tool") as native,
            patch.object(identity, "_verify_interpreter") as verify,
            patch.object(identity.subprocess, "run", return_value=subprocess.CompletedProcess([], 37)) as app,
        ):
            self.assertEqual(self.run_app("--value", "two words"), 37)
        record = identity._load_record(self.project)
        self.assertEqual(record["state"], "ready")
        self.assertNotIn("catalog_version", record)
        self.assertFalse((self.project / "pyproject.toml").exists())
        self.assertEqual(record["interpreter"]["version"], "3.12.10")
        self.assertIn("runtime", native.call_args.args[0][2])
        self.assertNotIn("-v", native.call_args.args[0])
        install.assert_called_once()
        verify.assert_called_once()
        self.assertEqual(app.call_args.args[0], [record["entry"], str(self.project / "app.py"),
                                              "--value", "two words"])
        self.assertEqual(app.call_args.kwargs["env"]["VIRTUAL_ENV"],
                         str(Path(record["root"]) / "venv"))

    def test_reuse_stays_offline_even_after_the_wheel_catalog_changes(self):
        record = self.record()
        (self.project / "app.py").write_text("print('source edit')\n", encoding="utf-8")
        with (
            patch.object(identity, "_packages", return_value=[self.package()]),
            patch.object(runtime, "CATALOG_URL", "https://raw.githubusercontent.com/astral-sh/uv/new-snapshot"),
            patch.object(runtime, "CATALOG_SHA256", "b" * 64),
            patch.object(runtime, "urlopen", side_effect=AssertionError("Reuse must stay offline")) as fetch,
            patch.object(identity, "resolve_distribution") as resolve,
            patch.object(identity, "prepare_runtime") as prepare,
            patch.object(identity, "synchronize_dependencies") as install,
            patch.object(identity, "_verify_interpreter"),
            patch.object(identity.subprocess, "run", return_value=subprocess.CompletedProcess([], 0)),
        ):
            self.assertEqual(self.run_app(), 0)
        resolve.assert_not_called()
        fetch.assert_not_called()
        prepare.assert_not_called()
        install.assert_not_called()
        self.assertEqual(identity._load_record(self.project), record)

    def test_default_run_ignores_requirements_and_shows_targeted_manual_install_hint(self):
        (self.project / "requirements.txt").write_text("--require-hashes\n-r missing.txt\n", encoding="utf-8")
        diagnostics = io.StringIO()
        with (
            patch.object(identity, "_packages", side_effect=[[], [], [self.package()]]),
            patch.object(identity, "resolve_distribution", return_value=self.distribution),
            patch.object(identity, "prepare_runtime", side_effect=self.prepare_files),
            patch.object(identity, "synchronize_dependencies") as install,
            patch.object(identity, "run_tool"),
            patch.object(identity, "_verify_interpreter"),
            patch.object(identity.subprocess, "run", return_value=subprocess.CompletedProcess([], 37)) as launch,
            redirect_stderr(diagnostics),
        ):
            self.assertEqual(self.run_app(requirements=None), 37)
        self.assertIsNone(install.call_args.args[2].requirements)
        launch.assert_called_once()
        record = identity._load_record(self.project)
        self.assertIn(identity.installation_hint(Path(record["entry"])), diagnostics.getvalue())
        self.assertEqual(diagnostics.getvalue().count("Identity Python:"), 1)

    def test_unselected_application_dependency_changes_do_not_update_identity(self):
        record = self.record(requirements=None)
        (self.project / "requirements.txt").write_text("not a pip requirement!\n", encoding="utf-8")
        (self.project / "uv.lock").write_text("not parsed", encoding="utf-8")
        (self.project / "pyproject.toml").write_text('[project]\ndependencies=["requests"]\n', encoding="utf-8")
        with (
            patch.object(identity, "_packages", return_value=[self.package()]),
            patch.object(identity, "resolve_distribution") as resolve,
            patch.object(identity, "prepare_runtime") as prepare,
            patch.object(identity, "synchronize_dependencies") as install,
            patch.object(identity, "_verify_interpreter"),
            patch.object(identity.subprocess, "run", return_value=subprocess.CompletedProcess([], 0)),
        ):
            self.assertEqual(self.run_app(requirements=None), 0)
        resolve.assert_not_called()
        prepare.assert_not_called()
        install.assert_not_called()
        self.assertEqual(identity._load_record(self.project), record)

    def test_preparation_and_clear_preserve_pyproject_bytes(self):
        source = self.project / "pyproject.toml"
        before = (
            b'\xef\xbb\xbf# application\r\n[project]\r\nname = "example"\r\n'
            b'version = "1.0"\r\ndependencies = []\r\n[tool.other]\r\nkeep = true\r\n'
        )
        source.write_bytes(before)
        with (
            patch.object(identity, "resolve_distribution", return_value=self.distribution) as resolve,
            patch.object(identity, "_packages", side_effect=[[], [], [self.package()]]),
            patch.object(identity, "prepare_runtime", side_effect=self.prepare_files),
            patch.object(identity, "synchronize_dependencies"),
            patch.object(identity, "run_tool"),
            patch.object(identity, "_verify_interpreter"),
            patch.object(identity.subprocess, "run", return_value=subprocess.CompletedProcess([], 0)),
        ):
            self.assertEqual(self.run_app(), 0)
        resolve.assert_called_once_with(self.info)
        self.assertEqual(source.read_bytes(), before)
        with (
            patch.object(identity, "_packages", side_effect=[[self.package()], []]),
            patch.object(identity, "_powershell", return_value=""),
        ):
            self.assertEqual(identity.clear_identity(self.project), 0)
        self.assertEqual(source.read_bytes(), before)

    def test_catalog_checksum_failure_prevents_runtime_preparation_and_registration(self):
        with (
            patch.object(identity, "_packages", return_value=[]),
            patch.object(runtime, "urlopen", return_value=io.BytesIO(b"modified catalog")) as fetch,
            patch.object(identity, "prepare_runtime") as prepare,
            patch.object(identity, "run_tool") as native,
            self.assertRaisesRegex(WinappError, "catalog SHA-256 mismatch"),
        ):
            self.run_app()
        fetch.assert_called_once()
        prepare.assert_not_called()
        native.assert_not_called()
        self.assertFalse((self.project / "pyproject.toml").exists())
        self.assertFalse(identity._record_path(self.project).exists())
        self.assertFalse((self.project / ".winapp" / identity.ROOT_NAME).exists())

    def test_failed_catalog_download_does_not_create_project_configuration_or_runtime(self):
        with (
            patch.object(identity, "_packages", return_value=[]),
            patch.object(runtime, "urlopen", side_effect=OSError("network offline")) as fetch,
            patch.object(identity, "prepare_runtime") as prepare,
            self.assertRaisesRegex(WinappError, "network offline"),
        ):
            self.run_app()
        fetch.assert_called_once()
        prepare.assert_not_called()
        self.assertFalse((self.project / "pyproject.toml").exists())
        self.assertFalse(identity._record_path(self.project).exists())

    def test_missing_exact_python_does_not_create_project_configuration(self):
        with (
            patch.object(identity, "_packages", return_value=[]),
            patch.object(identity, "resolve_distribution", side_effect=WinappError("No exact PBS")),
            patch.object(identity, "prepare_runtime") as prepare,
            self.assertRaisesRegex(WinappError, "No exact PBS"),
        ):
            self.run_app()
        prepare.assert_not_called()
        self.assertFalse((self.project / "pyproject.toml").exists())

    def test_explicit_project_metadata_changed_during_setup_aborts_before_registration(self):
        source = self.project / "pyproject.toml"
        source.write_text('[project]\nname="example"\nversion="1.0"\n', encoding="utf-8")

        def change_metadata(*args, **kwargs):
            source.write_text('[project]\nname="changed"\nversion="2.0"\n', encoding="utf-8")

        with (
            patch.object(identity, "_packages", return_value=[]),
            patch.object(identity, "resolve_distribution", return_value=self.distribution),
            patch.object(identity, "prepare_runtime", side_effect=self.prepare_files),
            patch.object(identity, "synchronize_dependencies", side_effect=change_metadata),
            patch.object(identity, "run_tool") as native,
            self.assertRaisesRegex(WinappError, "inputs changed"),
        ):
            self.run_app()
        native.assert_not_called()
        self.assertFalse(identity._record_path(self.project).exists())
        self.assertIn('name="changed"', source.read_text(encoding="utf-8"))

    def test_identity_setup_progress_and_verbose_tools_do_not_pollute_script_stdout(self):
        output = io.StringIO()
        diagnostics = io.StringIO()
        payload = '{"identity": "ready"}\n'

        def run_script(*arguments, **kwargs):
            print(payload, end="")
            return subprocess.CompletedProcess([], 37)

        with (
            patch.object(identity, "_packages", side_effect=[[], [], [self.package()]]),
            patch.object(identity, "resolve_distribution", return_value=self.distribution),
            patch.object(identity, "prepare_runtime", side_effect=self.prepare_files),
            patch.object(identity, "synchronize_dependencies", side_effect=lambda *a, **kw: print("installer status")),
            patch.object(identity, "run_tool", side_effect=lambda *a, **kw: print("native verbose output")) as native,
            patch.object(identity, "_verify_interpreter"),
            patch.object(identity.subprocess, "run", side_effect=run_script) as launch,
            redirect_stdout(output), redirect_stderr(diagnostics),
        ):
            self.assertEqual(identity.run_with_identity(
                self.project, Path("app.py"), [], Path("winapp.exe"), None,
                requirements_option=Path("requirements.txt"), verbose=True,
            ), 37)
        self.assertEqual(output.getvalue(), payload)
        for message in ("Preparing development identity", "installer status", "native verbose output", "Running app.py"):
            self.assertIn(message, diagnostics.getvalue())
        self.assertEqual(native.call_args.args[0][-1], "-v")
        self.assertNotIn("stdout", launch.call_args.kwargs)
        self.assertNotIn("stderr", launch.call_args.kwargs)

    def test_reused_identity_keeps_script_stdout_separate_from_status(self):
        self.record()
        output = io.StringIO()
        diagnostics = io.StringIO()
        payload = '{"reuse": true}\n'

        def run_script(*arguments, **kwargs):
            print(payload, end="")
            return subprocess.CompletedProcess([], 0)

        with (
            patch.object(identity, "_packages", return_value=[self.package()]),
            patch.object(identity, "_verify_interpreter"),
            patch.object(identity.subprocess, "run", side_effect=run_script),
            redirect_stdout(output), redirect_stderr(diagnostics),
        ):
            self.assertEqual(self.run_app(), 0)
        self.assertEqual(output.getvalue(), payload)
        self.assertIn("Reusing development identity", diagnostics.getvalue())
        self.assertIn("Running app.py", diagnostics.getvalue())

    def test_quiet_identity_keeps_application_streams_and_exit_status(self):
        output = io.StringIO()
        diagnostics = io.StringIO()

        def run_script(*arguments, **kwargs):
            print('{"identity": "ready"}')
            print("application error", file=sys.stderr)
            return subprocess.CompletedProcess([], 37)

        with (
            patch.object(identity, "_packages", side_effect=[[], [], [self.package()]]),
            patch.object(identity, "resolve_distribution", return_value=self.distribution),
            patch.object(identity, "prepare_runtime", side_effect=self.prepare_files),
            patch.object(identity, "synchronize_dependencies", side_effect=lambda *a, **kw: print("installer status")),
            patch.object(identity, "run_tool", side_effect=lambda *a, **kw: print("native status")),
            patch.object(identity, "_verify_interpreter"),
            patch.object(identity.subprocess, "run", side_effect=run_script) as launch,
            redirect_stdout(output), redirect_stderr(diagnostics),
        ):
            self.assertEqual(identity.run_with_identity(
                self.project, Path("app.py"), [], Path("winapp.exe"), None,
                requirements_option=Path("requirements.txt"), quiet=True,
            ), 37)
        self.assertEqual(output.getvalue(), '{"identity": "ready"}\n')
        self.assertEqual(diagnostics.getvalue(), "application error\n")
        self.assertNotIn("stdout", launch.call_args.kwargs)
        self.assertNotIn("stderr", launch.call_args.kwargs)

    def test_changed_python_version_architecture_or_abi_rebuilds_owned_identity(self):
        record = self.record()
        for info in (
            Interpreter("3.12.11", "x86_64"),
            Interpreter("3.13.2", "x86_64"),
            Interpreter("3.13.2", "aarch64"),
            Interpreter("3.13.2", "x86_64", True),
            Interpreter("3.13.2", "x86_64", False, True),
        ):
            with self.subTest(interpreter=info):
                record = self.assert_update(record, info=info)

    def test_changed_requirements_update_in_place_without_touching_manual_packages(self):
        record = self.record()
        development = self.project / ".venv" / "keep.txt"
        development.parent.mkdir()
        development.write_text("keep", encoding="utf-8")
        bindings = self.project / ".winapp" / "python" / "keep.py"
        bindings.parent.mkdir()
        bindings.write_text("keep bindings", encoding="utf-8")
        manual = Path(record["entry"]).parent.parent / "Lib" / "site-packages" / "manual.py"
        manual.parent.mkdir(parents=True)
        manual.write_text("# user package\n", encoding="utf-8")
        (self.project / "requirements.txt").write_text("requests\n", encoding="utf-8")
        self.assert_update(record)
        self.assertEqual(development.read_text(), "keep")
        self.assertEqual(bindings.read_text(), "keep bindings")
        self.assertEqual(manual.read_text(), "# user package\n")

    def test_changed_included_requirements_update_in_place(self):
        included = self.project / "dependencies.txt"
        included.write_text("packaging\n", encoding="utf-8")
        (self.project / "requirements.txt").write_text("-r dependencies.txt\n", encoding="utf-8")
        record = self.record()
        included.write_text("requests\n", encoding="utf-8")
        self.assert_update(record)

    def test_changed_explicit_project_metadata_updates_dependencies_in_place(self):
        record = self.record()
        source = self.project / "pyproject.toml"
        source.write_text(
            '[project]\nname = "identity-test"\nversion = "1.0"\nrequires-python = ">=3.11"\n',
            encoding="utf-8",
        )
        self.assert_update(record)

    def test_changed_sdk_pins_update_registration_without_rebuilding_python(self):
        record = self.record()
        configuration = self.project / "winapp.yaml"
        configuration.write_text(
            "packages:\n  - name: Microsoft.WindowsAppSDK\n    version: 2.5.1\n", encoding="utf-8",
        )
        self.assert_update(record)

    def test_identity_placeholders_are_rejected_before_registration_or_preparation(self):
        manifest = self.project / "Package.appxmanifest"
        original = manifest.read_text(encoding="utf-8")
        for before, after in (
            ('Name="LocalPythonProbe"', 'Name="Example.$targetnametoken$"'),
            ('Publisher="CN=LocalPythonProbe"', 'Publisher="CN=$targetentrypoint$"'),
        ):
            with self.subTest(attribute=after):
                manifest.write_text(original.replace(before, after), encoding="utf-8")
                with (
                    patch.object(identity, "_packages") as packages,
                    patch.object(identity, "resolve_distribution") as resolve,
                    patch.object(identity, "run_tool") as native,
                    self.assertRaisesRegex(WinappError, "literal"),
                ):
                    self.run_app()
                packages.assert_not_called()
                resolve.assert_not_called()
                native.assert_not_called()

    def test_changed_binding_dependencies_rebuilds_and_keeps_generated_sources(self):
        self.write_config()
        with patch("winappcli.generate.run_tool", side_effect=self.fake_codegen):
            bindings = generate(self.config(), Path("codegen.exe"), self.codegen_version)
        record = self.record()
        metadata = bindings / "pyproject.toml"

        def changed_codegen(arguments, project, **kwargs):
            self.fake_codegen(arguments, project, **kwargs)
            generated_metadata = Path(arguments[arguments.index("--output") + 1]) / "pyproject.toml"
            generated_metadata.write_text(
                generated_metadata.read_text(encoding="utf-8").replace(
                    'dependencies = [', 'dependencies = ["packaging", ',
                ), encoding="utf-8",
            )
            return ""

        with patch("winappcli.generate.run_tool", side_effect=changed_codegen):
            generate(self.config(), Path("codegen.exe"), self.codegen_version, force=True)
        updated = metadata.read_text(encoding="utf-8")
        self.assert_update(record)
        self.assertEqual(metadata.read_text(encoding="utf-8"), updated)
        self.assertTrue((bindings / "src" / "myapp_winrt" / "Uri.py").is_file())

    def test_changed_manifest_updates_registration_without_rebuilding_python(self):
        manifest = self.project / "Package.appxmanifest"
        record = self.record()
        for before, after in (
            ('Version="1.0.0.0"', 'Version="2.0.0.0"'),
            ('Name="LocalPythonProbe"', 'Name="NewPythonProbe"'),
            ('Publisher="CN=LocalPythonProbe"', 'Publisher="CN=NewPythonProbe"'),
        ):
            manifest.write_text(manifest.read_text(encoding="utf-8").replace(before, after), encoding="utf-8")
            record = self.assert_update(record)

    def test_unavailable_new_python_keeps_old_owned_identity(self):
        record = self.record()
        for selected in (Interpreter("3.13.2", "x86_64"), Interpreter("3.12.10", "x86_64", debug=True)):
            with (
                self.subTest(interpreter=selected),
                patch.object(identity, "probe_interpreter", return_value=selected),
                patch.object(identity, "_packages", return_value=[self.package()]),
                patch.object(identity, "resolve_distribution", side_effect=WinappError("No exact PBS distribution")) as resolve,
                patch.object(identity, "_powershell") as remove,
                patch.object(identity, "prepare_runtime") as prepare,
                patch.object(identity, "_verify_interpreter") as verify,
                patch.object(identity.subprocess, "run") as launch,
                self.assertRaisesRegex(WinappError, "No exact PBS"),
            ):
                self.run_app()
            resolve.assert_called_once_with(selected)
            remove.assert_not_called()
            prepare.assert_not_called()
            verify.assert_not_called()
            launch.assert_not_called()
            self.assertEqual(identity._load_record(self.project), record)
            self.assertTrue(Path(record["entry"]).is_file())

    def test_incompatible_project_python_keeps_old_owned_identity(self):
        record = self.record()
        (self.project / "pyproject.toml").write_text('[project]\nrequires-python=">=3.13"\n', encoding="utf-8")
        with (
            patch.object(identity, "_powershell") as remove,
            self.assertRaisesRegex(WinappError, "selected environment uses 3.12.10"),
        ):
            self.run_app()
        remove.assert_not_called()
        self.assertEqual(identity._load_record(self.project), record)

    def test_changed_inputs_do_not_hide_private_runtime_tampering(self):
        record = self.record()
        Path(record["base"]).with_name("python312.dll").write_bytes(b"changed")
        (self.project / "requirements.txt").write_text("requests\n", encoding="utf-8")
        with (
            patch.object(identity, "_powershell") as remove,
            self.assertRaisesRegex(WinappError, "owned PBS runtime"),
        ):
            self.run_app()
        remove.assert_not_called()
        self.assertEqual(identity._load_record(self.project), record)

    def test_rebuild_refuses_foreign_registration_for_new_manifest_name(self):
        record = self.record()
        manifest = self.project / "Package.appxmanifest"
        manifest.write_text(
            manifest.read_text(encoding="utf-8").replace('Name="LocalPythonProbe"', 'Name="NewPythonProbe"'),
            encoding="utf-8",
        )
        foreign = {**self.package(), "Name": "NewPythonProbe.debug"}
        with (
            patch.object(identity, "_packages", side_effect=[[self.package()], [foreign]]),
            patch.object(identity, "_powershell") as remove,
            patch.object(identity, "resolve_distribution") as resolve,
            self.assertRaisesRegex(WinappError, "refusing to replace"),
        ):
            self.run_app()
        remove.assert_not_called()
        resolve.assert_not_called()
        self.assertEqual(identity._load_record(self.project), record)

    def test_rebuild_refuses_registration_replaced_during_cleanup(self):
        record = self.record()
        manifest = self.project / "Package.appxmanifest"
        manifest.write_text(manifest.read_text().replace('Version="1.0.0.0"', 'Version="2.0.0.0"'))
        foreign = {**self.package(), "PackageFullName": "LocalPythonProbe.debug_2.0.0.0_x64__other"}
        with (
            patch.object(identity, "_packages", side_effect=[[self.package()], [foreign]]),
            patch.object(identity, "_powershell") as remove,
            self.assertRaisesRegex(WinappError, "registration disappeared"),
        ):
            self.run_app()
        remove.assert_not_called()
        self.assertEqual(identity._load_record(self.project), record)
        self.assertTrue(Path(record["entry"]).is_file())

    def test_rebuild_unregister_failure_keeps_owned_files_and_record(self):
        record = self.record()
        manifest = self.project / "Package.appxmanifest"
        manifest.write_text(manifest.read_text().replace('Version="1.0.0.0"', 'Version="2.0.0.0"'))
        with (
            patch.object(identity, "_packages", return_value=[self.package()]),
            patch.object(identity, "_powershell", return_value=""),
            patch.object(identity, "_verify_interpreter"),
            self.assertRaisesRegex(WinappError, "no files were removed"),
        ):
            self.run_app()
        self.assertEqual(identity._load_record(self.project), record)
        self.assertTrue(Path(record["entry"]).is_file())

    def test_failed_dependency_update_preserves_environment_without_launching_it(self):
        record = self.record()
        (self.project / "requirements.txt").write_text("requests\n", encoding="utf-8")
        with (
            patch.object(identity, "_packages", return_value=[self.package()]),
            patch.object(identity, "_powershell", return_value=""),
            patch.object(identity, "prepare_runtime", side_effect=self.prepare_files),
            patch.object(identity, "synchronize_dependencies", side_effect=WinappError("dependency conflict")),
            patch.object(identity, "run_tool") as native,
            patch.object(identity.subprocess, "run") as app,
            self.assertRaisesRegex(WinappError, "dependency conflict"),
        ):
            self.run_app()
        native.assert_not_called()
        app.assert_not_called()
        self.assertEqual(identity._load_record(self.project), record)
        self.assertTrue(Path(record["entry"]).is_file())
        self.assertFalse((self.project / "pyproject.toml").exists())

    def test_failed_runtime_restore_keeps_old_registration_and_manual_packages(self):
        record = self.record()
        manual = Path(record["entry"]).parent.parent / "manual-package.txt"
        manual.write_text("keep", encoding="utf-8")
        selected = Interpreter("3.12.11", "x86_64")
        distribution = Distribution(selected, self.distribution.build, self.distribution.url, self.distribution.sha256)
        with (
            patch.object(identity, "probe_interpreter", return_value=selected),
            patch.object(identity, "_packages", return_value=[self.package(record)]),
            patch.object(identity, "resolve_distribution", return_value=distribution),
            patch.object(identity, "prepare_runtime", side_effect=self.prepare_files),
            patch.object(identity, "synchronize_dependencies", side_effect=WinappError("cannot restore user package")),
            patch.object(identity, "_powershell") as registrations,
            patch.object(identity, "run_tool") as native,
            self.assertRaisesRegex(WinappError, "cannot restore user package"),
        ):
            self.run_app()
        registrations.assert_not_called()
        native.assert_not_called()
        self.assertEqual(identity._load_record(self.project), record)
        self.assertEqual(manual.read_text(), "keep")
        self.assertEqual(list(Path(record["root"]).glob("venv-*")), [])

    def test_registration_failure_restores_old_binary_manifest_and_registration(self):
        record = self.record()
        original_binary = Path(record["base"]).read_bytes()
        location = Path(self.package(record)["InstallLocation"])
        original_manifest = (location / "AppxManifest.xml").read_bytes()
        manifest = self.project / "Package.appxmanifest"
        manifest.write_text(manifest.read_text().replace('Version="1.0.0.0"', 'Version="2.0.0.0"'))
        registry = [self.package(record)]
        actions = []

        def powershell(script, **kwargs):
            nonlocal registry
            actions.append(script)
            if script == identity.REMOVE_PACKAGE:
                registry = []
            elif script == identity.REGISTER_PACKAGE:
                self.assertEqual(kwargs["manifest"], location / "AppxManifest.xml")
                self.assertEqual(kwargs["external"], Path(record["base"]).parent)
                self.assertEqual(Path(record["base"]).read_bytes(), original_binary)
                self.assertEqual((location / "AppxManifest.xml").read_bytes(), original_manifest)
                registry = [self.package(record)]
            return ""

        def fail_native(*args, **kwargs):
            nonlocal registry
            Path(record["base"]).write_bytes(b"modified executable")
            (location / "AppxManifest.xml").write_text("new registration files")
            registry = [self.package(record)]
            raise WinappError("registration failed after changing files")

        with (
            patch.object(identity, "_packages", side_effect=lambda _: list(registry)),
            patch.object(identity, "_powershell", side_effect=powershell),
            patch.object(identity, "run_tool", side_effect=fail_native),
            patch.object(identity, "_verify_interpreter"),
            self.assertRaisesRegex(WinappError, "registration failed"),
        ):
            self.run_app()
        self.assertEqual(identity._load_record(self.project), record)
        self.assertEqual(Path(record["base"]).read_bytes(), original_binary)
        self.assertIn(identity.REGISTER_PACKAGE, actions)
        self.assertEqual(list(Path(record["root"]).glob(".registration-*")), [])

    def test_interrupted_update_keeps_backup_and_recovers_before_next_run(self):
        record = self.record()
        manifest = self.project / "Package.appxmanifest"
        original = manifest.read_bytes()
        manifest.write_bytes(original.replace(b'Version="1.0.0.0"', b'Version="2.0.0.0"'))
        registry = [self.package(record)]

        def powershell(script, **kwargs):
            nonlocal registry
            registry = [] if script == identity.REMOVE_PACKAGE else [self.package(record)]
            return ""

        with (
            patch.object(identity, "_packages", side_effect=lambda _: list(registry)),
            patch.object(identity, "_powershell", side_effect=powershell),
            patch.object(identity, "run_tool", side_effect=WinappError("native failed")),
            patch.object(identity, "_rollback_update", side_effect=WinappError("recovery interrupted")),
            self.assertRaisesRegex(WinappError, "Rollback could not finish"),
        ):
            self.run_app()
        pending = identity._load_record(self.project)
        self.assertEqual(pending["state"], "updating")
        self.assertEqual(pending["previous"], record)
        self.assertTrue(Path(pending["backup"]).is_dir())
        self.assertTrue(Path(record["entry"]).exists())
        manifest.write_bytes(original)
        with (
            patch.object(identity, "_packages", side_effect=lambda _: list(registry)),
            patch.object(identity, "_powershell", side_effect=powershell),
            patch.object(identity, "_verify_interpreter"),
            patch.object(identity.subprocess, "run", return_value=subprocess.CompletedProcess([], 0)),
            patch.object(identity, "run_tool") as native,
        ):
            self.assertEqual(self.run_app(), 0)
        native.assert_not_called()
        self.assertEqual(identity._load_record(self.project), record)
        self.assertFalse(Path(pending["backup"]).exists())

    def test_update_journal_cannot_restore_or_delete_an_outside_backup(self):
        record = self.record()
        identity._write_record(self.project, {
            **record, "state": "updating", "previous": record,
            "backup": str(self.root / "outside"), "old_location": self.package(record)["InstallLocation"],
        })
        with self.assertRaises(WinappError):
            identity._load_record(self.project)

    def test_private_dll_or_venv_cfg_tampering_is_detected(self):
        record = self.record()
        dll = Path(record["base"]).with_name("python312.dll")
        dll.write_bytes(b"changed")
        with self.assertRaisesRegex(WinappError, "owned PBS runtime"):
            self.run_app()

    def test_unknown_existing_registration_is_never_replaced(self):
        with patch.object(identity, "_packages", return_value=[self.package()]):
            with patch.object(identity, "run_tool") as native:
                with self.assertRaisesRegex(WinappError, "refusing to replace"):
                    self.run_app()
        native.assert_not_called()

    def test_missing_developer_mode_fails_before_runtime_download(self):
        with (
            patch.object(identity, "_packages", return_value=[]),
            patch.object(identity, "_require_developer_mode", side_effect=WinappError("Developer Mode required")),
            patch.object(identity, "resolve_distribution") as resolve,
        ):
            with self.assertRaisesRegex(WinappError, "Developer Mode required"):
                self.run_app()
        resolve.assert_not_called()

    def test_missing_registration_is_not_repaired_implicitly(self):
        self.record()
        with patch.object(identity, "_packages", return_value=[]):
            with self.assertRaisesRegex(WinappError, "package is missing"):
                self.run_app()

    def test_dependency_failure_removes_preparation_without_registering(self):
        with (
            patch.object(identity, "_packages", return_value=[]),
            patch.object(identity, "resolve_distribution", return_value=self.distribution),
            patch.object(identity, "prepare_runtime", side_effect=self.prepare_files),
            patch.object(identity, "synchronize_dependencies", side_effect=WinappError("dependency conflict")),
            patch.object(identity, "run_tool") as native,
        ):
            with self.assertRaisesRegex(WinappError, "dependency conflict"):
                self.run_app()
        native.assert_not_called()
        self.assertFalse(identity._record_path(self.project).exists())
        self.assertFalse((self.project / ".winapp" / identity.ROOT_NAME).exists())

    def test_native_registration_failure_cleans_only_verified_new_package(self):
        with (
            patch.object(identity, "_packages", side_effect=[[], [], [self.package()], []]),
            patch.object(identity, "resolve_distribution", return_value=self.distribution),
            patch.object(identity, "prepare_runtime", side_effect=self.prepare_files),
            patch.object(identity, "synchronize_dependencies"),
            patch.object(identity, "run_tool", side_effect=WinappError("native verification failed")),
            patch.object(identity, "_powershell", return_value="") as remove,
        ):
            with self.assertRaisesRegex(WinappError, "native verification failed"):
                self.run_app()
        self.assertEqual(remove.call_args.kwargs["full_name"], self.package()["PackageFullName"])
        self.assertIn("-PreserveApplicationData", remove.call_args.args[0])
        self.assertFalse(identity._record_path(self.project).exists())

    def test_wrong_identity_after_registration_is_rolled_back_not_marked_ready(self):
        with (
            patch.object(identity, "_packages", side_effect=[[], [], [self.package()],
                                                           [self.package()], []]),
            patch.object(identity, "resolve_distribution", return_value=self.distribution),
            patch.object(identity, "prepare_runtime", side_effect=self.prepare_files),
            patch.object(identity, "synchronize_dependencies"),
            patch.object(identity, "run_tool"),
            patch.object(identity, "_verify_interpreter", side_effect=WinappError("wrong identity")),
            patch.object(identity, "_powershell", return_value=""),
        ):
            with self.assertRaisesRegex(WinappError, "wrong identity"):
                self.run_app()
        self.assertFalse(identity._record_path(self.project).exists())
        self.assertFalse((self.project / ".winapp" / identity.ROOT_NAME).exists())

    def test_unowned_registration_after_failed_preparation_is_not_removed(self):
        package = self.package()
        package["InstallLocation"] = str(self.root / "another-project")
        with (
            patch.object(identity, "_packages", side_effect=[[], [], [package]]),
            patch.object(identity, "resolve_distribution", return_value=self.distribution),
            patch.object(identity, "prepare_runtime", side_effect=self.prepare_files),
            patch.object(identity, "synchronize_dependencies"),
            patch.object(identity, "run_tool", side_effect=WinappError("native failed")),
            patch.object(identity, "_powershell") as remove,
        ):
            with self.assertRaisesRegex(WinappError, "Cleanup could not finish"):
                self.run_app()
        remove.assert_not_called()
        self.assertTrue(identity._record_path(self.project).exists())

    def test_clear_preserves_app_data_and_never_touches_development_env(self):
        record = self.record()
        development = self.project / ".venv" / "keep.txt"
        development.parent.mkdir()
        development.write_text("keep", encoding="utf-8")
        with (
            patch.object(identity, "_packages", side_effect=[[self.package()], []]),
            patch.object(identity, "_powershell", return_value="") as remove,
            patch.object(identity, "_verify_interpreter") as verify,
            patch.object(identity.subprocess, "run") as process,
        ):
            self.assertEqual(identity.clear_identity(self.project), 0)
        verify.assert_not_called()
        process.assert_not_called()
        self.assertIn("-PreserveApplicationData", remove.call_args.args[0])
        self.assertEqual(development.read_text(), "keep")
        self.assertFalse(Path(record["root"]).exists())
        self.assertFalse((self.project / "pyproject.toml").exists())

    def test_clear_incomplete_preparation_does_not_start_python(self):
        record = self.record()
        record.update(state="preparing", full_name=None, registration_attempted=False)
        identity._write_record(self.project, record)
        with (
            patch.object(identity, "_packages") as packages,
            patch.object(identity, "_verify_interpreter") as verify,
            patch.object(identity.subprocess, "run") as process,
        ):
            self.assertEqual(identity.clear_identity(self.project), 0)
        packages.assert_not_called()
        verify.assert_not_called()
        process.assert_not_called()
        self.assertFalse(Path(record["root"]).exists())
        self.assertFalse(identity._record_path(self.project).exists())

    def test_clear_interrupted_update_preserves_state_without_recovery_or_execution(self):
        record = self.record()
        backup = Path(record["root"]) / (".registration-" + "b" * 32)
        backup.mkdir()
        saved = backup / "keep.txt"
        saved.write_text("recovery files", encoding="utf-8")
        for attempted in (False, True):
            pending = {
                **record, "state": "updating", "full_name": None,
                "registration_attempted": attempted, "previous": record,
                "backup": str(backup), "old_location": self.package(record)["InstallLocation"],
            }
            identity._write_record(self.project, pending)
            before = identity._record_path(self.project).read_bytes()
            with (
                self.subTest(registration_attempted=attempted),
                patch.object(identity, "_rollback_update") as rollback,
                patch.object(identity, "_clear_owned_files") as clear,
                patch.object(identity, "_packages") as packages,
                patch.object(identity, "_verify_interpreter") as verify,
                patch.object(identity.subprocess, "run") as process,
                self.assertRaisesRegex(WinappError, "Identity update is incomplete") as failure,
            ):
                identity.clear_identity(self.project)
            for operation in (rollback, clear, packages, verify, process):
                operation.assert_not_called()
            self.assertIn("project you trust", str(failure.exception))
            self.assertIn("winapp run --identity", str(failure.exception))
            self.assertEqual(identity._record_path(self.project).read_bytes(), before)
            self.assertEqual(saved.read_text(encoding="utf-8"), "recovery files")
            self.assertTrue(Path(record["entry"]).is_file())

    def test_clear_can_unregister_after_private_environment_was_deleted(self):
        record = self.record()
        import shutil
        shutil.rmtree(record["root"])
        with (
            patch.object(identity, "_packages", side_effect=[[self.package()], []]),
            patch.object(identity, "_powershell", return_value=""),
        ):
            self.assertEqual(identity.clear_identity(self.project), 0)
        self.assertFalse(identity._record_path(self.project).exists())

    def test_tampered_record_cannot_delete_another_directory(self):
        record = self.record()
        record["root"] = str(self.project)
        identity._write_record(self.project, record)
        with patch.object(identity, "_packages") as packages:
            with self.assertRaisesRegex(WinappError, "outside"):
                identity.clear_identity(self.project)
        packages.assert_not_called()

    def test_active_operation_blocks_clear_and_second_run(self):
        self.record()
        (self.project / "requirements.txt").write_text("requests\n", encoding="utf-8")
        with identity._operation_lock(self.project):
            with self.assertRaisesRegex(WinappError, "in use"):
                identity.clear_identity(self.project)
            with patch.object(identity, "_powershell") as remove:
                with self.assertRaisesRegex(WinappError, "in use"):
                    self.run_app()
            remove.assert_not_called()

    def test_shared_registration_lock_is_case_insensitive_and_blocks_clear(self):
        record = self.record()
        with identity._registration_lock("LOCALPYTHONPROBE.DEBUG"):
            with self.assertRaisesRegex(WinappError, "being updated"):
                with identity._registration_lock(record["package"]):
                    self.fail("The same identity acquired a second lock.")
            with patch.object(identity, "_powershell") as remove:
                with self.assertRaisesRegex(WinappError, "being updated"):
                    identity.clear_identity(self.project)
            remove.assert_not_called()
        self.assertEqual(identity._load_record(self.project), record)
        self.assertTrue(Path(record["entry"]).is_file())

    def test_concurrent_projects_cannot_replace_each_others_registration(self):
        clone = self.root / "clone"
        clone.mkdir()
        for name in ("Package.appxmanifest", "requirements.txt", "app.py"):
            shutil.copyfile(self.project / name, clone / name)
        worker = r'''
from pathlib import Path
import json, subprocess, sys, time
from unittest.mock import patch
from winappcli import identity
from winappcli.errors import WinappError
from winappcli.python_runtime import Distribution, Interpreter

project, shared = Path(sys.argv[1]), Path(sys.argv[2])
label = sys.argv[3]
registry = shared / "registration.json"
info = Interpreter("3.12.10", "x86_64")
distribution = Distribution(info, "20250529",
    "https://github.com/astral-sh/python-build-standalone/releases/download/20250529/"
    "cpython-3.12.10%2B20250529-x86_64-pc-windows-msvc-install_only_stripped.tar.gz", "a" * 64)

def wait_for(name):
    deadline = time.monotonic() + 15
    while not (shared / name).exists():
        if time.monotonic() > deadline:
            raise WinappError(f"Test timed out waiting for {name}")
        time.sleep(0.02)

def prepare(project, root, distribution, **kwargs):
    venv = root / "venv"
    runtime = venv / "runtime"
    runtime.mkdir(parents=True)
    (runtime / "python.exe").write_bytes(b"runtime")
    (runtime / "python312.dll").write_bytes(b"dll")
    (venv / "Scripts").mkdir()
    (venv / "Scripts" / "python.exe").write_bytes(b"launcher")
    (venv / "pyvenv.cfg").write_text("test", encoding="utf-8")
    return venv, runtime, venv / "Scripts" / "python.exe"

def install(*args, **kwargs):
    (shared / f"prepared-{label}").touch()
    wait_for(f"install-{label}")

def register(*args, **kwargs):
    (shared / f"registering-{label}").touch()
    if label == "A":
        wait_for("finish-A")
    if registry.exists():
        (shared / "replaced-registration").touch()
    registry.write_text(json.dumps([{
        "Name": "LocalPythonProbe.debug", "Publisher": "CN=LocalPythonProbe",
        "PackageFullName": "LocalPythonProbe.debug_1.0.0.0_x64__abc",
        "InstallLocation": str(project / ".winapp" / identity.ROOT_NAME),
        "IsDevelopmentMode": True,
    }]), encoding="utf-8")

with (
    patch.object(identity.Path, "home", return_value=shared),
    patch.object(identity, "select_identity_python", return_value=Path(sys.executable)),
    patch.object(identity, "probe_interpreter", return_value=info),
    patch.object(identity, "_require_developer_mode"),
    patch.object(identity, "resolve_distribution", return_value=distribution),
    patch.object(identity, "prepare_runtime", side_effect=prepare),
    patch.object(identity, "synchronize_dependencies", side_effect=install),
    patch.object(identity, "_packages", side_effect=lambda _: json.loads(registry.read_text()) if registry.exists() else []),
    patch.object(identity, "run_tool", side_effect=register),
    patch.object(identity, "_verify_interpreter"),
    patch.object(identity.subprocess, "run", return_value=subprocess.CompletedProcess([], 0)),
):
    try:
        result = identity.run_with_identity(project, Path("app.py"), [], Path("winapp.exe"), None,
                                            requirements_option=Path("requirements.txt"))
    except WinappError as error:
        print(f"BLOCKED: {error}", flush=True)
        sys.exit(23)
    sys.exit(result)
'''
        processes = [
            subprocess.Popen([sys.executable, "-I", "-X", "utf8", "-c", worker, str(project), str(self.root), label],
                             stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8")
            for project, label in ((self.project, "A"), (clone, "B"))
        ]

        def wait_for(name):
            deadline = time.monotonic() + 15
            while not (self.root / name).exists():
                self.assertLess(time.monotonic(), deadline, f"Worker did not reach {name}")
                time.sleep(0.02)

        try:
            wait_for("prepared-A")
            wait_for("prepared-B")
            (self.root / "install-A").touch()
            wait_for("registering-A")
            (self.root / "install-B").touch()
            output, error = processes[1].communicate(timeout=15)
            self.assertEqual(processes[1].returncode, 23, output + error)
            self.assertIn("BLOCKED:", output)
            self.assertFalse((self.root / "registering-B").exists())
            (self.root / "finish-A").touch()
            output, error = processes[0].communicate(timeout=15)
            self.assertEqual(processes[0].returncode, 0, output + error)
            self.assertFalse((self.root / "replaced-registration").exists())
            package = json.loads((self.root / "registration.json").read_text())[0]
            self.assertEqual(package["InstallLocation"], str(self.project / ".winapp" / identity.ROOT_NAME))
        finally:
            for name in ("install-A", "install-B", "finish-A"):
                (self.root / name).touch()
            for process in processes:
                if process.poll() is None:
                    try:
                        process.communicate(timeout=15)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        process.communicate()

    def test_probe_checks_real_image_base_prefix_and_version(self):
        record = self.record()
        output = {
            "code": 0, "name": record["full_name"], "prefix": str(Path(record["root"]) / "venv"),
            "base_prefix": str(Path(record["root"]) / "venv" / "runtime"),
            "image": record["base"], "version": "3.12.10", "platform": "win-amd64",
            "free_threaded": False, "debug": False,
        }
        with patch.object(identity.subprocess, "run",
                          return_value=subprocess.CompletedProcess([], 0, json.dumps(output), "")) as probe:
            identity._verify_interpreter(self.project, record)
        self.assertEqual(probe.call_args.args[0][:3], [record["entry"], "-I", "-c"])
        for field, value in (("base_prefix", str(self.root / "unrelated-python")), ("debug", True)):
            with (
                self.subTest(field=field),
                patch.object(identity.subprocess, "run",
                             return_value=subprocess.CompletedProcess([], 0, json.dumps({**output, field: value}), "")),
                self.assertRaisesRegex(WinappError, "wrong identity or environment"),
            ):
                identity._verify_interpreter(self.project, record)

    def test_malformed_identity_probe_does_not_escape_as_a_type_error(self):
        record = self.record()
        for output in ("[]", '{"prefix": null, "code": 0}'):
            with self.subTest(output=output):
                with patch.object(identity.subprocess, "run",
                                  return_value=subprocess.CompletedProcess([], 0, output, "")):
                    with self.assertRaisesRegex(WinappError, "invalid identity state"):
                        identity._verify_interpreter(self.project, record)
