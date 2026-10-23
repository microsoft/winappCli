# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

from pathlib import Path
import os
import subprocess
import sys
import tomllib
from packaging.version import Version
from unittest.mock import patch

from winappcli import dependencies as deps
from winappcli.errors import WinappError
from winappcli.python_runtime import Interpreter

from helpers import ProjectTest


class DependencyTests(ProjectTest):
    def setUp(self):
        super().setUp()
        installer = patch.object(deps, "installer_environment", return_value={
            "PIP_DEFAULT_TIMEOUT": "60", "UV_HTTP_TIMEOUT": "60",
        })
        installer.start()
        self.addCleanup(installer.stop)

    def pyproject(self, *, uv=False, extra=""):
        (self.project / "pyproject.toml").write_text(
            '[project]\nname = "example"\nversion = "0.0.0"\n'
            'dependencies = [\n    "packaging==26.3", # keep application comment\n]\n'
            '[dependency-groups]\ndev = ["pytest"]\n'
            + ("[tool.uv]\n" if uv else "") + extra, encoding="utf-8",
        )

    def test_requirements_registration_is_persistent_separated_and_idempotent(self):
        source = self.project / "requirements.txt"
        source.write_text("# application\npackaging==26.3\n", encoding="utf-8")
        dev = self.project / "requirements-dev.txt"
        dev.write_text("# development\npytest\n", encoding="utf-8")
        deps.register_dependencies(self.project, "myapp_winrt")
        self.assertNotIn("dynwinrt", source.read_text())
        self.assertIn(r"-e '.\.winapp\python\myapp_winrt'", source.read_text())
        self.assertNotIn("codegen", source.read_text())
        self.assertIn("-r requirements.txt", dev.read_text())
        self.assertNotIn("dynwinrt", dev.read_text())
        before = (source.read_bytes(), dev.read_bytes())
        deps.register_dependencies(self.project, "myapp_winrt")
        self.assertEqual(before, (source.read_bytes(), dev.read_bytes()))

    def test_new_script_project_uses_requirements_without_inventing_package_metadata(self):
        for name in ("pyproject.toml", "requirements.txt", "requirements-dev.txt"):
            (self.project / name).unlink()
        deps.register_dependencies(self.project, "myapp_winrt")
        self.assertEqual(deps.detect_dependencies(self.project).kind, "requirements")
        self.assertFalse((self.project / "pyproject.toml").exists())
        self.assertTrue((self.project / "requirements.txt").exists())
        self.assertFalse((self.project / "requirements-dev.txt").exists())

    def test_application_include_does_not_gain_a_reverse_development_include(self):
        source = self.project / "requirements.txt"
        development = self.project / "requirements-dev.txt"
        source.write_text("-r requirements-dev.txt\n", encoding="utf-8")
        development.write_text("# development\npytest\n", encoding="utf-8")
        before = development.read_bytes()

        deps.register_dependencies(self.project, "myapp_winrt")

        self.assertEqual(development.read_bytes(), before)
        self.assertNotIn("-r requirements.txt", development.read_text())
        self.assertIn(r"-e '.\.winapp\python\myapp_winrt'", source.read_text())
        snapshot = (source.read_bytes(), development.read_bytes())
        deps.register_dependencies(self.project, "myapp_winrt")
        self.assertEqual(snapshot, (source.read_bytes(), development.read_bytes()))

    def test_nested_application_include_does_not_create_a_requirements_cycle(self):
        source = self.project / "requirements.txt"
        development = self.project / "requirements-dev.txt"
        directory = self.project / "deps"
        directory.mkdir()
        included = directory / "application.txt"
        source.write_text("-r deps\\application.txt\n", encoding="utf-8")
        included.write_text("-r ..\\requirements-dev.txt\n", encoding="utf-8")
        development.write_text("pytest\n", encoding="utf-8")

        deps.register_dependencies(self.project, "myapp_winrt")

        self.assertEqual(development.read_text(), "pytest\n")
        self.assertIn(development, deps.requirements_files(self.project, source))
        self.assertIn(r"-e '.\.winapp\python\myapp_winrt'", source.read_text())

    def test_unchanged_requirements_without_final_newline_are_preserved(self):
        source = self.project / "requirements.txt"
        included = self.project / "application.txt"
        development = self.project / "requirements-dev.txt"
        for prefix, newline in ((b"", b"\n"), (b"\xef\xbb\xbf", b"\r\n")):
            with self.subTest(prefix=prefix, newline=newline):
                included.write_bytes(prefix + b"# keep" + newline + b"packaging==26.3")
                source.write_bytes(b"-r application.txt" + newline + b"-e '.\\.winapp\\python\\myapp_winrt'")
                development.write_bytes(b"-r requirements.txt")
                before = {path: path.read_bytes() for path in (source, included, development)}
                with patch.object(deps.os, "replace", wraps=os.replace) as replace:
                    deps.register_dependencies(self.project, "myapp_winrt")
                self.assertEqual({path: path.read_bytes() for path in before}, before)
                replace.assert_not_called()

    def test_requirements_addition_gets_a_separating_newline_only_when_needed(self):
        source = self.project / "requirements.txt"
        for newline in (b"\n", b"\r\n"):
            with self.subTest(newline=newline):
                before = b"# application" + newline + b"packaging==26.3"
                source.write_bytes(before)
                deps.register_dependencies(self.project, "myapp_winrt")
                self.assertEqual(
                    source.read_bytes(), before + newline + b"-e '.\\.winapp\\python\\myapp_winrt'" + newline,
                )

    def test_registration_removes_managed_declarations_and_preserves_included_comments(self):
        source = self.project / "requirements.txt"
        source.write_text("-r application.txt\n", encoding="utf-8")
        included = self.project / "application.txt"
        included.write_text("dynwinrt==0.1.0rc20 # preserve note\n", encoding="utf-8")
        deps.register_dependencies(self.project, "myapp_winrt")
        self.assertEqual(included.read_text(), "# preserve note\n")
        self.assertNotIn("dynwinrt==", source.read_text())

    def test_continued_requirements_are_inspected_and_preserved_during_registration(self):
        source = self.project / "requirements.txt"
        application = b"packaging==26.3 \\\r\n    ; python_version >= '3.11' # keep marker\r\n"
        source.write_bytes(
            b"\xef\xbb\xbf# application\r\n" + application
            + b"dynwinrt==0.1.0rc21 \\\r\n    # runtime note\r\n",
        )
        deps.register_dependencies(self.project, "myapp_winrt")
        self.assertTrue(source.read_bytes().startswith(b"\xef\xbb\xbf# application\r\n" + application))
        self.assertIn(b"# runtime note\r\n", source.read_bytes())
        self.assertNotIn(b"dynwinrt", source.read_bytes())
        before = source.read_bytes()
        deps.register_dependencies(self.project, "myapp_winrt")
        self.assertEqual(source.read_bytes(), before)

    def test_continued_requirements_include_is_resolved_from_its_own_directory(self):
        directory = self.project / "deps"
        directory.mkdir()
        source = self.project / "requirements.txt"
        source.write_text("-r \\\n    deps\\application.txt\n", encoding="utf-8")
        included = directory / "application.txt"
        included.write_text("dynwinrt==\\\n    0.1.0rc21\n", encoding="utf-8")
        self.assertIn(included, deps.requirements_files(self.project, source))
        deps.register_dependencies(self.project, "myapp_winrt")
        self.assertEqual(included.read_text(), "")

    def test_standard_pyproject_uses_standard_fields_not_uv_sources(self):
        self.pyproject()
        source = self.project / "pyproject.toml"
        deps.register_dependencies(self.project, "myapp_winrt")
        document = tomllib.loads(source.read_text())
        self.assertEqual(document["project"]["dependencies"], ["packaging==26.3"])
        self.assertEqual(document["dependency-groups"]["dev"], ["pytest"])
        self.assertNotIn("myapp-winrt", document["project"]["dependencies"])
        self.assertNotIn("tool", document)
        self.assertIn("# keep application comment", source.read_text())

    def test_uv_records_the_local_editable_source_and_preserves_other_dependencies(self):
        self.pyproject(uv=True)
        source = self.project / "pyproject.toml"
        deps.register_dependencies(self.project, "myapp_winrt")
        document = tomllib.loads(source.read_text())
        self.assertIn("myapp-winrt", document["project"]["dependencies"])
        self.assertEqual(document["tool"]["uv"]["sources"]["myapp-winrt"], {
            "path": r".\.winapp\python\myapp_winrt", "editable": True,
        })
        before = source.read_bytes()
        deps.register_dependencies(self.project, "myapp_winrt")
        self.assertEqual(source.read_bytes(), before)

    def test_uv_source_conflict_does_not_modify_project(self):
        self.pyproject(uv=True, extra='[tool.uv.sources]\nmyapp-winrt = { git = "https://example.com/project" }\n')
        source = self.project / "pyproject.toml"
        before = source.read_bytes()
        with self.assertRaisesRegex(WinappError, "different source"):
            deps.register_dependencies(self.project, "myapp_winrt")
        self.assertEqual(source.read_bytes(), before)

    def test_uv_registration_preserves_binding_conditions_and_source_settings(self):
        source = self.project / "pyproject.toml"
        for table in (
            "[tool.uv.sources]\n"
            "myapp_winrt = { path = '.\\.winapp\\python\\myapp_winrt', editable = true, "
            "marker = \"sys_platform == 'win32'\" } # keep source comment\n",
            "[tool.uv.sources.myapp_winrt]\n"
            "path = '.\\.winapp\\python\\myapp_winrt'\n"
            "editable = true\n"
            "marker = \"sys_platform == 'win32'\" # keep source comment\n",
        ):
            with self.subTest(table=table):
                source.write_text(
                    '[project]\nname = "example"\n'
                    'dependencies = ["myapp_winrt[feature]>=0.1; sys_platform == \'win32\'"] # keep dependency\n'
                    + table, encoding="utf-8",
                )
                before = source.read_bytes()
                deps.register_dependencies(self.project, "myapp_winrt")
                deps.register_dependencies(self.project, "myapp_winrt")
                self.assertEqual(source.read_bytes(), before)

    def test_uv_registration_only_updates_editable_in_an_existing_source(self):
        self.pyproject(
            uv=True,
            extra="[tool.uv.sources]\nmyapp-winrt = { path = '.\\.winapp\\python\\myapp_winrt', "
            "editable = false, marker = \"sys_platform == 'win32'\" }\n",
        )
        deps.register_dependencies(self.project, "myapp_winrt")
        source = tomllib.loads((self.project / "pyproject.toml").read_text())["tool"]["uv"]["sources"]["myapp-winrt"]
        self.assertEqual(source["marker"], "sys_platform == 'win32'")
        self.assertTrue(source["editable"])

    def test_uv_registration_rejects_direct_binding_urls_without_editing(self):
        source = self.project / "pyproject.toml"
        source.write_text(
            '[project]\nname = "example"\n'
            'dependencies = ["myapp-winrt @ https://example.com/bindings.whl"]\n[tool.uv]\n',
            encoding="utf-8",
        )
        before = source.read_bytes()
        with self.assertRaisesRegex(WinappError, "direct URL"):
            deps.register_dependencies(self.project, "myapp_winrt")
        self.assertEqual(source.read_bytes(), before)

    def test_uv_registration_rejects_multiple_sources_without_editing(self):
        self.pyproject(
            uv=True, extra="[tool.uv.sources]\nmyapp-winrt = "
            "[{ path = '.\\.winapp\\python\\myapp_winrt', marker = \"sys_platform == 'win32'\" }]\n",
        )
        source = self.project / "pyproject.toml"
        before = source.read_bytes()
        with self.assertRaisesRegex(WinappError, "different source"):
            deps.register_dependencies(self.project, "myapp_winrt")
        self.assertEqual(source.read_bytes(), before)

    def test_registration_respects_normalized_group_and_uv_source_names(self):
        source = self.project / "pyproject.toml"
        source.write_text(
            '[project]\nname="example"\ndependencies=[]\n'
            '[dependency-groups]\nDev=["pytest"]\n'
            "[tool.uv.sources]\nmyapp_winrt={path='.\\.winapp\\python\\myapp_winrt', editable=true}\n",
            encoding="utf-8",
        )
        deps.register_dependencies(self.project, "myapp_winrt")
        document = tomllib.loads(source.read_text())
        self.assertEqual(set(document["dependency-groups"]), {"Dev"})
        self.assertEqual(set(document["tool"]["uv"]["sources"]), {"myapp_winrt"})
        self.assertEqual(document["dependency-groups"]["Dev"], ["pytest"])

    def test_short_requirements_include_without_space_is_hashed_and_resolved(self):
        source = self.project / "requirements.txt"
        source.write_text("-rapplication.txt\n", encoding="utf-8")
        included = self.project / "application.txt"
        included.write_text("dynwinrt==0.1.0rc21\n", encoding="utf-8")
        self.assertIn(included, deps.requirements_files(self.project, source))
        deps.register_dependencies(self.project, "myapp_winrt")
        self.assertEqual(included.read_text(), "")

    def test_uv_lockfile_is_sufficient_to_detect_uv_without_tool_table(self):
        self.pyproject()
        (self.project / "uv.lock").write_text("version = 1\n", encoding="utf-8")
        self.assertEqual(deps.detect_dependencies(self.project).kind, "uv")

    def test_other_managed_projects_are_not_bypassed_with_pip(self):
        self.pyproject()
        for filename in ("poetry.lock", "pdm.lock", "Pipfile"):
            with self.subTest(filename=filename):
                path = self.project / filename
                path.touch()
                with self.assertRaisesRegex(WinappError, "not supported"):
                    deps.detect_dependencies(self.project)
                path.unlink()

    def test_dynamic_dependencies_are_not_replaced(self):
        (self.project / "pyproject.toml").write_text(
            '[project]\nname="example"\ndynamic=["dependencies"]\n', encoding="utf-8",
        )
        with self.assertRaisesRegex(WinappError, "Dynamic"):
            deps.register_dependencies(self.project, "myapp_winrt")

    def test_generated_or_hash_locked_requirements_are_not_edited(self):
        source = self.project / "requirements.txt"
        for content in ("# This file is autogenerated by pip-compile\npackaging\n",
                        "packaging==26.3 --hash=sha256:abc\n"):
            with self.subTest(content=content):
                source.write_text(content, encoding="utf-8")
                with self.assertRaisesRegex(WinappError, "generated or hash-locked"):
                    deps.register_dependencies(self.project, "myapp_winrt")
                self.assertEqual(source.read_text(), content)

    def test_standard_project_registration_does_not_create_development_dependencies(self):
        source = self.project / "pyproject.toml"
        source.write_text('[project]\nname="example"\ndependencies=[]\n', encoding="utf-8")
        before = source.read_bytes()
        deps.register_dependencies(self.project, "myapp_winrt")
        self.assertEqual(source.read_bytes(), before)

    def test_managed_packages_are_removed_without_changing_application_dependencies(self):
        source = self.project / "pyproject.toml"
        source.write_text(
            '[project]\nname="example"\ndependencies=["dynwinrt==0.1.0rc21", "packaging"]\n'
            '[dependency-groups]\nbuild=["dynwinrt-codegen==0.1.0rc21", "build"]\n'
            'dev=[{include-group="build"}, "pytest"]\n', encoding="utf-8",
        )
        deps.register_dependencies(self.project, "myapp_winrt")
        document = tomllib.loads(source.read_text())
        self.assertEqual(document["project"]["dependencies"], ["packaging"])
        self.assertEqual(document["dependency-groups"]["build"], ["build"])
        self.assertEqual(document["dependency-groups"]["dev"], [{"include-group": "build"}, "pytest"])

    def test_standard_dev_group_includes_are_supported_and_cycles_fail(self):
        (self.project / "pyproject.toml").write_text(
            '[project]\nname="example"\ndependencies=[]\n'
            '[dependency-groups]\nbuild=["dynwinrt-codegen==0.1.0rc21"]\n'
            'dev=[{include-group="build"}]\n', encoding="utf-8",
        )
        self.assertEqual(deps.development_requirements(self.project), ["dynwinrt-codegen==0.1.0rc21"])
        source = self.project / "pyproject.toml"
        source.write_text(source.read_text().replace('build=["dynwinrt-codegen==0.1.0rc21"]',
                                                   'build=[{include-group="dev"}]'), encoding="utf-8")
        with self.assertRaisesRegex(WinappError, "Cyclic"):
            deps.development_requirements(self.project)

    def test_invalid_non_dev_dependency_is_reported_without_a_traceback_or_edits(self):
        source = self.project / "pyproject.toml"
        source.write_text(
            '[project]\nname="example"\ndependencies=[]\n'
            '[dependency-groups]\nbuild=["not a valid dependency"]\n', encoding="utf-8",
        )
        before = source.read_bytes()
        with self.assertRaisesRegex(WinappError, "Invalid dependency in .*build"):
            deps.register_dependencies(self.project, "myapp_winrt")
        self.assertEqual(source.read_bytes(), before)

    def test_pip_install_targets_explicit_project_python_and_combines_bindings(self):
        python = self.project / ".venv" / "Scripts" / "python.exe"
        bindings = self.project / ".winapp" / "python" / "myapp_winrt"
        with patch.object(deps, "run_tool") as run:
            deps.synchronize_project(self.project, python, bindings)
        command = run.call_args_list[0].args[0]
        self.assertEqual(command[:7], [sys.executable, "-I", "-m", "pip", "--python", str(python), "install"])
        self.assertEqual(command[-2:], ["--editable", str(bindings)])
        self.assertIn(str(self.project / "requirements-dev.txt"), command)
        self.assertEqual(run.call_args_list[-1].args[0][-1], "check")

    def test_private_requirements_install_excludes_development_dependencies(self):
        python = self.project / "identity" / "Scripts" / "python.exe"
        with patch.object(deps, "run_tool") as run:
            deps.synchronize_project(self.project, python, None, private=True)
        self.assertIn(str(self.project / "requirements.txt"), run.call_args_list[0].args[0])
        self.assertNotIn(str(self.project / "requirements-dev.txt"), run.call_args_list[0].args[0])

    def test_independent_development_requirements_preserve_application_requirements(self):
        application = self.project / "requirements.txt"
        development = self.project / "requirements-dev.txt"
        application.write_text("packaging==26.3\n", encoding="utf-8")
        development.write_text("pytest\n", encoding="utf-8")
        python = self.project / ".venv" / "Scripts" / "python.exe"
        with patch.object(deps, "run_tool") as run:
            deps.synchronize_project(self.project, python, None)
        command = run.call_args_list[0].args[0]
        inputs = [command[index + 1] for index, value in enumerate(command) if value == "-r"]
        self.assertEqual(inputs, [str(application), str(development)])

    def test_plain_pyproject_install_does_not_require_building_the_application(self):
        self.pyproject()
        with patch.object(deps, "run_tool") as run:
            deps.synchronize_project(self.project, Path("project-python.exe"), Path("bindings"))
        command = run.call_args_list[0].args[0]
        self.assertIn("packaging==26.3", command)
        self.assertIn("pytest", command)
        self.assertNotIn(str(self.project), command)

    def test_uv_synchronization_uses_uv_and_the_selected_environment(self):
        self.pyproject(uv=True)
        python = self.project / ".venv" / "Scripts" / "python.exe"
        with (
            patch.object(deps, "uv_executable", return_value="uv.exe"),
            patch.object(deps, "run_tool") as run,
        ):
            deps.synchronize_project(self.project, python, None)
        call = run.call_args_list[0]
        self.assertEqual(call.args[0][:2], ["uv.exe", "sync"])
        self.assertIn("dev", call.args[0])
        self.assertEqual(call.kwargs["environment"]["UV_PROJECT_ENVIRONMENT"], str(python.parent.parent))
        self.assertNotIn("--upgrade", call.args[0])
        self.assertNotIn("--inexact", call.args[0])

    def test_uv_sync_rejects_binding_name_or_source_mismatches_before_installing(self):
        source = self.project / "pyproject.toml"
        bindings = self.project / ".winapp" / "python" / "renamed_bindings"
        for dependency, entry in (
            ("myapp-winrt", "renamed-bindings = { path = '.\\.winapp\\python\\renamed_bindings', editable = true }"),
            ("renamed-bindings", "myapp-winrt = { path = '.\\.winapp\\python\\myapp_winrt', editable = true }"),
            ("renamed-bindings", "renamed-bindings = { path = '.\\.winapp\\python\\myapp_winrt', editable = true }"),
            ("renamed-bindings", "renamed-bindings = { path = '.\\.winapp\\python\\renamed_bindings', editable = false }"),
            ("renamed-bindings @ https://example.com/bindings.whl",
             "renamed-bindings = { path = '.\\.winapp\\python\\renamed_bindings', editable = true }"),
            ("renamed-bindings", "renamed-bindings = [{ path = '.\\.winapp\\python\\renamed_bindings' }]"),
        ):
            source.write_text(
                f'[project]\nname="example"\ndependencies=["{dependency}"]\n[tool.uv.sources]\n{entry}\n',
                encoding="utf-8",
            )
            before = source.read_bytes()
            for private in (False, True):
                with (
                    self.subTest(dependency=dependency, entry=entry, private=private),
                    patch.object(deps, "uv_executable", return_value="uv.exe"),
                    patch.object(deps, "run_tool") as run,
                ):
                    with self.assertRaisesRegex(WinappError, "renamed_bindings.*does not match"):
                        deps.synchronize_project(self.project, Path("python.exe"), bindings, private=private)
                    run.assert_not_called()
                    self.assertEqual(source.read_bytes(), before)

    def test_uv_sync_accepts_matching_conditional_bindings_without_rewriting_them(self):
        source = self.project / "pyproject.toml"
        source.write_text(
            '[project]\nname="example"\ndependencies=["myapp_winrt; sys_platform == \'win32\'"]\n'
            "[tool.uv.sources]\nmyapp_winrt = { path = '.\\.winapp\\python\\myapp_winrt', "
            "editable = true, marker = \"sys_platform == 'win32'\" }\n", encoding="utf-8",
        )
        before = source.read_bytes()
        (self.project / "uv.lock").write_text("version=1\n", encoding="utf-8")
        for private in (False, True):
            with (
                self.subTest(private=private),
                patch.object(deps, "uv_executable", return_value="uv.exe"),
                patch.object(deps, "run_tool") as run,
            ):
                deps.synchronize_project(
                    self.project, Path("python.exe"), self.project / ".winapp" / "python" / "myapp_winrt",
                    private=private,
                )
                self.assertEqual(run.call_args_list[0].args[0][:2], ["uv.exe", "sync"])
                self.assertEqual("--locked" in run.call_args_list[0].args[0], private)
                self.assertEqual(source.read_bytes(), before)

    def test_uv_synchronization_preserves_the_running_tool_environment(self):
        self.pyproject(uv=True)
        python = self.project / ".venv" / "Scripts" / "python.exe"
        with (
            patch.object(deps.sys, "prefix", str(python.parent.parent)),
            patch.object(deps, "uv_executable", return_value="uv.exe"),
            patch.object(deps, "run_tool") as run,
        ):
            deps.synchronize_project(self.project, python, None)
        self.assertIn("--inexact", run.call_args_list[0].args[0])

    def test_private_uv_sync_is_locked_without_development_groups_or_application_install(self):
        self.pyproject(uv=True)
        (self.project / "uv.lock").write_text("version=1\n", encoding="utf-8")
        python = self.project / ".winapp" / "python-identity" / "venv" / "Scripts" / "python.exe"
        with (
            patch.object(deps, "uv_executable", return_value="uv.exe"),
            patch.object(deps, "run_tool") as run,
        ):
            deps.synchronize_project(self.project, python, None, private=True)
        command = run.call_args_list[0].args[0]
        self.assertIn("--locked", command)
        self.assertIn("--no-default-groups", command)
        self.assertIn("--no-install-project", command)
        self.assertNotIn("--inexact", command)

    def test_uv_without_development_groups_does_not_request_a_missing_group(self):
        (self.project / "pyproject.toml").write_text(
            '[project]\nname="example"\ndependencies=[]\n[tool.uv]\n', encoding="utf-8",
        )
        with (
            patch.object(deps, "uv_executable", return_value="uv.exe"),
            patch.object(deps, "run_tool") as run,
        ):
            deps.synchronize_project(self.project, Path("project-python.exe"), None)
        self.assertNotIn("--group", run.call_args_list[0].args[0])

    def test_codegen_always_resolves_latest_including_previews_without_project_pins(self):
        python = self.project / ".venv" / "Scripts" / "python.exe"
        declarations = (self.project / "pyproject.toml").read_bytes()
        with patch.object(deps, "run_tool") as run:
            deps.install_codegen(self.project, python, None)
        self.assertEqual(run.call_args.args[0][5], str(python))
        self.assertIn("--upgrade", run.call_args.args[0])
        self.assertIn("--force-reinstall", run.call_args.args[0])
        self.assertIn("--pre", run.call_args.args[0])
        self.assertEqual(run.call_args.args[0][-1], "dynwinrt-codegen")
        self.assertEqual((self.project / "pyproject.toml").read_bytes(), declarations)
        self.pyproject(uv=True)
        with (
            patch.object(deps, "uv_executable", return_value="uv.exe"),
            patch.object(deps, "run_tool") as run,
        ):
            deps.install_codegen(self.project, python, None)
        self.assertEqual(run.call_args.args[0][:3], ["uv.exe", "pip", "install"])
        self.assertIn("--upgrade", run.call_args.args[0])
        self.assertIn("--reinstall", run.call_args.args[0])
        self.assertEqual(run.call_args.args[0][-3:], ["--prerelease", "allow", "dynwinrt-codegen"])

    def test_latest_codegen_install_failure_is_not_hidden_by_existing_packages(self):
        with (
            patch.object(deps, "run_tool", side_effect=WinappError("package source unavailable")),
            self.assertRaisesRegex(WinappError, "package source unavailable"),
        ):
            deps.install_codegen(self.project, self.project / ".venv" / "Scripts" / "python.exe", None)

    def test_project_codegen_install_is_exact_with_no_latest_resolution(self):
        python = self.project / ".venv" / "Scripts" / "python.exe"
        selected = Version("0.1.0rc23")
        with patch.object(deps, "run_tool") as run:
            deps.install_codegen(self.project, python, selected)
        command = run.call_args.args[0]
        self.assertEqual(command[-1], "dynwinrt-codegen==0.1.0rc23")
        self.assertNotIn("--upgrade", command)
        self.assertNotIn("--pre", command)
        self.pyproject(uv=True)
        with (
            patch.object(deps, "uv_executable", return_value="uv.exe"),
            patch.object(deps, "run_tool") as run,
        ):
            deps.install_codegen(self.project, python, selected)
        command = run.call_args.args[0]
        self.assertEqual(command[-1], "dynwinrt-codegen==0.1.0rc23")
        self.assertNotIn("--upgrade", command)
        self.assertNotIn("--prerelease", command)

    def test_matching_project_codegen_is_reused_without_install_or_network(self):
        python = self.project / ".venv" / "Scripts" / "python.exe"
        python.parent.mkdir(parents=True)
        launcher = python.with_name("dynwinrt-codegen.exe")
        launcher.write_bytes(b"codegen")
        with (
            patch.object(deps, "installed_codegen_version", return_value=self.codegen_version) as metadata,
            patch.object(deps, "run_tool") as run,
        ):
            deps.install_codegen(self.project, python, self.codegen_version)
        metadata.assert_called_once_with(self.project, python)
        run.assert_not_called()
        launcher.unlink()
        with patch.object(deps, "run_tool") as run:
            deps.install_codegen(self.project, python, self.codegen_version)
        self.assertEqual(run.call_args.args[0][-1], f"dynwinrt-codegen=={self.codegen_version}")

    def test_global_python_is_rejected_without_installing_packages(self):
        with (
            patch.object(deps, "select_project_python", return_value=Path(sys.executable)),
            patch.object(deps, "probe_interpreter", side_effect=WinappError("not a virtual environment")),
            patch.object(deps, "run_tool") as run,
            self.assertRaisesRegex(WinappError, "virtual environment"),
        ):
            deps.ensure_project_python(self.project)
        run.assert_not_called()

    def test_missing_project_environment_is_created_without_installing_into_tool_environment(self):
        python = self.project / ".venv" / "Scripts" / "python.exe"
        with (
            patch.object(deps, "probe_interpreter", return_value=Interpreter("3.12.10", "x86_64")),
            patch.object(deps, "run_tool") as run,
        ):
            self.assertEqual(deps.ensure_project_python(self.project), python)
        self.assertEqual(run.call_args.args[0][:5], [sys.executable, "-I", "-m", "venv", "--without-pip"])

    def test_project_module_cannot_replace_standard_library_venv(self):
        (self.project / "venv.py").write_text(
            "raise SystemExit('PROJECT_VENV_EXECUTED')\n", encoding="utf-8",
        )
        python = deps.ensure_project_python(self.project)
        self.assertTrue(python.is_file())
        self.assertEqual(python, self.project / ".venv" / "Scripts" / "python.exe")

    def test_empty_standard_project_only_checks_dependencies_without_running_empty_install(self):
        (self.project / "pyproject.toml").write_text(
            '[project]\nname="empty"\ndependencies=[]\n', encoding="utf-8",
        )
        with patch.object(deps, "run_tool") as run:
            deps.synchronize_project(self.project, Path(sys.executable), None)
        run.assert_called_once()
        self.assertEqual(run.call_args.args[0][-1], "check")


class UvExecutableTests(ProjectTest):
    def test_uv_lookup_only_probes_absolute_path_candidates(self):
        installed = self.root / "installed uv"
        executable = installed / "uv.exe"
        path = os.pathsep.join(("", ".", "relative-tools", r"C:tools", r"\tools", f'"{installed}"'))

        def which(candidate):
            self.assertTrue(Path(candidate).is_absolute(), candidate)
            return str(executable) if Path(candidate) == executable else None

        with patch.dict(os.environ, {"PATH": path}), patch.object(deps.shutil, "which", side_effect=which):
            self.assertEqual(deps.uv_executable(), str(executable))

    def test_real_uv_lookup_ignores_cwd_even_when_windows_allows_it(self):
        installed = self.root / "installed uv"
        installed.mkdir()
        (self.project / "uv.exe").write_bytes(b"not executed")
        (installed / "uv.exe").write_bytes(b"not executed")
        relative = self.project / "relative-tools"
        relative.mkdir()
        (relative / "uv.exe").write_bytes(b"not executed")
        script = (
            "import sys; sys.path.insert(0, sys.argv[1]); "
            "from winappcli.dependencies import uv_executable; "
            "print(uv_executable())"
        )
        environment = {key: value for key, value in os.environ.items()
                       if key.lower() != "nodefaultcurrentdirectoryinexepath"}
        environment["PATHEXT"] = ".EXE;.CMD"
        for available in (True, False):
            with self.subTest(available=available):
                environment["PATH"] = os.pathsep.join(
                    [str(installed)] if available else ["", ".", "relative-tools"],
                )
                result = subprocess.run(
                    [sys.executable, "-I", "-c", script, str(Path(deps.__file__).parents[1])],
                    cwd=self.project, env=environment, capture_output=True, text=True, timeout=30,
                )
                if available:
                    self.assertEqual(result.returncode, 0, result.stderr)
                    self.assertEqual(Path(result.stdout.strip()), installed / "uv.exe")
                else:
                    self.assertNotEqual(result.returncode, 0)
                    self.assertIn("uv is not available", result.stderr)

    def test_uv_lookup_keeps_path_order_and_supported_launcher_extensions(self):
        first = self.root / "first"
        second = self.root / "second"
        first.mkdir()
        second.mkdir()
        (first / "uv.exe").write_bytes(b"not executed")
        (second / "uv.exe").write_bytes(b"not executed")
        with patch.dict(os.environ, {"PATH": os.pathsep.join((str(first), str(second))), "PATHEXT": ".EXE;.CMD"}):
            self.assertEqual(Path(deps.uv_executable()), first / "uv.exe")
            (first / "uv.exe").unlink()
            self.assertEqual(Path(deps.uv_executable()), second / "uv.exe")
            (second / "uv.exe").unlink()
            (first / "uv.cmd").write_text("@exit /b 0\n", encoding="utf-8")
            self.assertEqual(Path(deps.uv_executable()), first / "uv.cmd")

    def test_uv_lookup_supports_launchers_with_python_311_qualified_lookup(self):
        installed = self.root / "installed uv"
        installed.mkdir()
        launcher = installed / "uv.cmd"
        launcher.write_text("@exit /b 0\n", encoding="utf-8")

        def qualified_lookup(candidate):
            self.assertTrue(Path(candidate).is_absolute(), candidate)
            return candidate if Path(candidate).is_file() else None

        with (
            patch.dict(os.environ, {"PATH": str(installed), "PATHEXT": ".EXE;.CMD"}),
            patch.object(deps.shutil, "which", side_effect=qualified_lookup),
        ):
            self.assertEqual(Path(deps.uv_executable()), launcher)


class InstallerTimeoutTests(ProjectTest):
    def setUp(self):
        super().setUp()
        environment = {name: value for name, value in os.environ.items()
                       if name not in {"PIP_TIMEOUT", "PIP_DEFAULT_TIMEOUT", "UV_HTTP_TIMEOUT"}}
        settings = patch.dict(os.environ, environment, clear=True)
        settings.start()
        self.addCleanup(settings.stop)
        self.python = self.project / ".venv" / "Scripts" / "python.exe"

    def test_pip_default_is_child_scoped_and_reaches_build_subprocesses(self):
        before = dict(os.environ)
        queried_timeout = []

        def configuration(*arguments, **options):
            queried_timeout.append(options["environment"].get("PIP_DEFAULT_TIMEOUT"))
            return "global.index-url='https://example.com/simple'\n"

        with patch.object(deps, "run_tool", side_effect=configuration) as query:
            child = deps.installer_environment(self.project, self.python)
        self.assertEqual(child["PIP_DEFAULT_TIMEOUT"], "60")
        self.assertTrue(dict(os.environ) == before, "Parent environment changed.")
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

    def test_uv_default_and_explicit_override_are_child_scoped(self):
        before = dict(os.environ)
        with patch.object(deps, "run_tool") as query:
            self.assertEqual(deps.installer_environment(self.project, self.python, uv=True)["UV_HTTP_TIMEOUT"], "60")
        query.assert_not_called()
        self.assertTrue(dict(os.environ) == before, "Parent environment changed.")
        with patch.dict(os.environ, {"UV_HTTP_TIMEOUT": "180"}):
            self.assertEqual(deps.installer_environment(self.project, self.python, uv=True)["UV_HTTP_TIMEOUT"], "180")

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
