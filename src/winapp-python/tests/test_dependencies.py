# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

from pathlib import Path
import os
import subprocess
import sys
from packaging.version import Version
from unittest.mock import patch

from winappcli import dependencies as deps
from winappcli.errors import WinappError
from winappcli.python_runtime import Interpreter

from helpers import ProjectTest


class DependencyTests(ProjectTest):
    def setUp(self):
        super().setUp()
        self.python = self.project / ".winapp" / ".codegen-test" / "Scripts" / "python.exe"
        installer = patch.object(deps, "installer_environment", return_value={"PIP_DEFAULT_TIMEOUT": "60"})
        installer.start()
        self.addCleanup(installer.stop)

    def test_generator_environment_uses_tool_python_not_application_settings(self):
        (self.project / "pyproject.toml").write_text(
            '[project]\nrequires-python=">=3.14"\ndynamic=["dependencies"]\n'
            '[tool.poetry]\n[tool.pdm]\n[tool.uv]\n', encoding="utf-8",
        )
        for name in ("poetry.lock", "pdm.lock", "Pipfile", "uv.lock"):
            (self.project / name).write_bytes(b"manager-owned data")
        before = {path: path.read_bytes() for path in self.project.iterdir() if path.is_file()}
        with (
            patch.object(deps, "probe_interpreter", return_value=Interpreter("3.12.10", "x86_64")) as probe,
            patch.object(deps, "run_tool") as run,
        ):
            with deps.temporary_codegen_python(self.project) as python:
                self.assertEqual(python.parent.parent.parent, self.project / ".winapp")
                self.assertTrue(python.parent.parent.name.startswith(".codegen-"))
                self.assertTrue(python.parent.parent.is_dir())
        self.assertEqual(run.call_args.args[0], [
            sys.executable, "-I", "-m", "venv", "--without-pip", str(python.parent.parent),
        ])
        probe.assert_called_once_with(python, self.project, require_venv=True)
        self.assertFalse(python.parent.parent.exists())
        self.assertFalse((self.project / ".venv").exists())
        self.assertEqual({path: path.read_bytes() for path in before}, before)

    def test_preseeded_environments_are_never_used_or_deleted(self):
        seeded = [
            self.project / ".winapp" / name / "Scripts" / "python.exe"
            for name in ("codegen", ".codegen-leftover")
        ]
        for path in seeded:
            path.parent.mkdir(parents=True)
            path.write_bytes(b"untrusted executable")
        created = []
        with (
            patch.object(deps, "probe_interpreter", return_value=Interpreter("3.12.10", "x86_64")) as probe,
            patch.object(deps, "run_tool") as run,
        ):
            for _ in range(2):
                with deps.temporary_codegen_python(self.project) as python:
                    self.assertNotIn(python, seeded)
                    self.assertEqual(list(python.parent.parent.iterdir()), [])
                    created.append(python)
                self.assertFalse(python.parent.parent.exists())
        self.assertNotEqual(created[0], created[1])
        self.assertEqual([call.args[0] for call in probe.call_args_list], created)
        self.assertEqual(run.call_count, 2)
        for call in run.call_args_list:
            self.assertEqual(call.args[0][0], sys.executable)
        for path in seeded:
            self.assertEqual(path.read_bytes(), b"untrusted executable")

    def test_temporary_environment_is_cleaned_on_creation_probe_and_caller_failure(self):
        directory = self.project / ".winapp"
        before = set(directory.iterdir())
        for phase in ("create", "probe", "caller", "interrupt"):
            with (
                self.subTest(phase=phase),
                patch.object(deps, "run_tool", side_effect=WinappError("creation failed") if phase == "create" else None),
                patch.object(deps, "probe_interpreter", side_effect=WinappError("probe failed") if phase == "probe" else None),
                self.assertRaises(KeyboardInterrupt if phase == "interrupt" else WinappError),
            ):
                with deps.temporary_codegen_python(self.project):
                    if phase == "interrupt":
                        raise KeyboardInterrupt
                    raise WinappError("caller failed")
            self.assertEqual(set(directory.iterdir()), before)

    def test_redirected_winapp_directory_is_rejected_before_creating_an_environment(self):
        import stat

        directory = self.project / ".winapp"
        original = Path.lstat

        def attributes(path, *args, **kwargs):
            value = original(path, *args, **kwargs)
            if path == directory:
                from types import SimpleNamespace
                return SimpleNamespace(st_mode=value.st_mode, st_file_attributes=stat.FILE_ATTRIBUTE_REPARSE_POINT)
            return value

        with (
            patch.object(Path, "lstat", attributes),
            patch.object(deps, "run_tool") as run,
            self.assertRaisesRegex(WinappError, "Symlinks and junctions"),
        ):
            with deps.temporary_codegen_python(self.project):
                self.fail("A redirected directory was accepted")
        run.assert_not_called()

    def test_project_module_cannot_replace_standard_library_venv(self):
        (self.project / "venv.py").write_text(
            "raise SystemExit('PROJECT_VENV_EXECUTED')\n", encoding="utf-8",
        )
        with deps.temporary_codegen_python(self.project) as python:
            self.assertTrue(python.is_file())
            self.assertFalse((self.project / ".venv").exists())
            result = subprocess.run(
                [str(python), "-I", "-c", "import importlib.util as u; assert u.find_spec('pip') is None"],
                capture_output=True, text=True, timeout=30,
            )
            self.assertEqual(result.returncode, 0, result.stderr)
        self.assertFalse(python.parent.parent.exists())

    def test_codegen_resolves_latest_including_previews_only_when_unpinned(self):
        before = (self.project / "pyproject.toml").read_bytes()
        with patch.object(deps, "run_tool") as run:
            deps.install_codegen(self.project, self.python, None)
        command = run.call_args.args[0]
        self.assertEqual(command[:7], [sys.executable, "-I", "-m", "pip", "--python", str(self.python), "install"])
        self.assertIn("--upgrade", command)
        self.assertIn("--force-reinstall", command)
        self.assertIn("--pre", command)
        self.assertEqual(command[-1], "dynwinrt-codegen")
        self.assertEqual((self.project / "pyproject.toml").read_bytes(), before)

    def test_codegen_install_is_exact_and_independent_of_project_manager(self):
        (self.project / "pyproject.toml").write_text("[tool.poetry]\n[tool.uv]\n", encoding="utf-8")
        with patch.object(deps, "run_tool") as run:
            deps.install_codegen(self.project, self.python, Version("0.1.0rc23"))
        command = run.call_args.args[0]
        self.assertEqual(command[:7], [sys.executable, "-I", "-m", "pip", "--python", str(self.python), "install"])
        self.assertEqual(command[-1], "dynwinrt-codegen==0.1.0rc23")
        self.assertNotIn("--upgrade", command)
        self.assertNotIn("--pre", command)

    def test_codegen_install_failure_is_not_hidden_by_existing_packages(self):
        with (
            patch.object(deps, "run_tool", side_effect=WinappError("package source unavailable")),
            self.assertRaisesRegex(WinappError, "package source unavailable"),
        ):
            deps.install_codegen(self.project, self.python, None)

    def test_requirements_includes_constraints_and_cycles_are_read_without_edits(self):
        directory = self.project / "deps"
        directory.mkdir()
        source = self.project / "requirements.txt"
        source.write_bytes(b"\xef\xbb\xbf-r \\\r\n    deps\\application.txt\r\n-cconstraints.txt")
        included = directory / "application.txt"
        included.write_text("-r ..\\requirements.txt\n", encoding="utf-8")
        constraints = self.project / "constraints.txt"
        constraints.write_text("packaging==26.3\n", encoding="utf-8")
        before = {path: path.read_bytes() for path in (source, included, constraints)}
        self.assertEqual(set(deps.requirements_files(self.project, source)), set(before))
        self.assertEqual({path: path.read_bytes() for path in before}, before)

    def test_hash_enforcement_is_rejected_in_included_requirements(self):
        source = self.project / "requirements.txt"
        source.write_text("-r extra.txt\n", encoding="utf-8")
        extra = self.project / "extra.txt"
        for content in ("--require-hashes\n", "packaging==26.3 \\\n    --hash=sha256:abc\n"):
            with self.subTest(content=content):
                extra.write_text(content, encoding="utf-8")
                with self.assertRaisesRegex(WinappError, "without hashes"):
                    deps.requirements_files(self.project, source)
                self.assertEqual(extra.read_text(), content)

    def test_comments_about_hashes_do_not_enable_enforcement(self):
        source = self.project / "requirements.txt"
        source.write_text("# export --without-hashes, not --require-hashes\npackaging # --hash=example\n", encoding="utf-8")
        self.assertEqual(deps.requirements_files(self.project, source), (source,))


class InstallerTimeoutTests(ProjectTest):
    def setUp(self):
        super().setUp()
        environment = {name: value for name, value in os.environ.items()
                       if name not in {"PIP_TIMEOUT", "PIP_DEFAULT_TIMEOUT"}}
        settings = patch.dict(os.environ, environment, clear=True)
        settings.start()
        self.addCleanup(settings.stop)
        self.python = self.project / ".winapp" / ".codegen-test" / "Scripts" / "python.exe"

    def test_pip_default_is_child_scoped_and_reaches_build_subprocesses(self):
        before = dict(os.environ)
        queried_timeout = []

        def configuration(*arguments, **options):
            queried_timeout.append(options["environment"].get("PIP_DEFAULT_TIMEOUT"))
            return "global.index-url='https://example.com/simple'\n"

        with patch.object(deps, "run_tool", side_effect=configuration) as query:
            child = deps.installer_environment(self.project, self.python)
        self.assertEqual(child["PIP_DEFAULT_TIMEOUT"], "60")
        self.assertEqual(dict(os.environ), before)
        self.assertEqual(queried_timeout, [None])
        self.assertEqual(query.call_args.args[0][-2:], ["config", "list"])
        self.assertEqual(query.call_args.args[0][5], str(self.python))

    def test_pip_explicit_environment_timeout_is_not_overwritten(self):
        for name in ("PIP_TIMEOUT", "PIP_DEFAULT_TIMEOUT"):
            with self.subTest(name=name), patch.dict(os.environ, {name: "135"}):
                with patch.object(deps, "run_tool") as query:
                    child = deps.installer_environment(self.project, self.python)
                query.assert_not_called()
                self.assertEqual(child[name], "135")
                if name == "PIP_TIMEOUT":
                    self.assertNotIn("PIP_DEFAULT_TIMEOUT", child)

    def test_pip_global_and_install_configuration_timeouts_are_respected(self):
        for key in ("global.timeout", "install.timeout", "global.default-timeout", "install.default-timeout"):
            with self.subTest(key=key), patch.object(deps, "run_tool", return_value=f"{key}='120'\n"):
                self.assertNotIn("PIP_DEFAULT_TIMEOUT", deps.installer_environment(self.project, self.python))

    def test_invalid_pip_configuration_is_not_silently_ignored(self):
        with (
            patch.object(deps, "run_tool", side_effect=WinappError("Invalid pip configuration")),
            self.assertRaisesRegex(WinappError, "Invalid pip configuration"),
        ):
            deps.installer_environment(self.project, self.python)

    def test_actual_pip_configuration_is_respected_without_changing_it(self):
        configuration = self.project / "pip.ini"
        configuration.write_text("[install]\ntimeout = 135\n", encoding="utf-8")
        before = configuration.read_bytes()
        with patch.dict(os.environ, {"PIP_CONFIG_FILE": str(configuration)}):
            child = deps.installer_environment(self.project, Path(sys.executable))
        self.assertNotIn("PIP_DEFAULT_TIMEOUT", child)
        self.assertEqual(configuration.read_bytes(), before)

    def test_project_module_cannot_replace_pip_configuration_query(self):
        (self.project / "pip.py").write_text(
            "raise SystemExit('PROJECT_PIP_EXECUTED')\n", encoding="utf-8",
        )
        with patch.dict(os.environ, {"PIP_CONFIG_FILE": os.devnull}):
            child = deps.installer_environment(self.project, Path(sys.executable))
        self.assertEqual(child["PIP_DEFAULT_TIMEOUT"], "60")

    def test_actual_isolated_build_backend_inherits_default_timeout(self):
        source = self.project / "build-probe"
        source.mkdir()
        (source / "pyproject.toml").write_text(
            '[build-system]\nrequires=[]\nbuild-backend="backend"\nbackend-path=["."]\n',
            encoding="utf-8",
        )
        (source / "backend.py").write_text(
            "import os\n"
            "def get_requires_for_build_wheel(config_settings=None):\n"
            "    raise RuntimeError('BUILD_TIMEOUT=' + os.environ['PIP_DEFAULT_TIMEOUT'])\n",
            encoding="utf-8",
        )
        with patch.dict(os.environ, {"PIP_CONFIG_FILE": os.devnull}):
            environment = deps.installer_environment(self.project, Path(sys.executable))
            result = subprocess.run(
                [sys.executable, "-I", "-m", "pip", "wheel", "--no-deps", "--disable-pip-version-check",
                 "--no-input", "--wheel-dir", str(self.project / "wheels"), str(source)],
                env=environment, capture_output=True, text=True, encoding="utf-8", errors="replace",
                check=False, timeout=60,
            )
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("BUILD_TIMEOUT=60", result.stdout + result.stderr)
