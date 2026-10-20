# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

import hashlib
import io
import json
from pathlib import Path
import subprocess
import tarfile
from unittest.mock import patch

from winappcli import python_runtime as runtime
from winappcli.errors import WinappError

from helpers import ProjectTest


class RuntimeTests(ProjectTest):
    def setUp(self):
        super().setUp()
        self.python = self.project / ".venv" / "Scripts" / "python.exe"
        self.python.parent.mkdir(parents=True)
        self.python.write_bytes(b"project launcher")
        self.info = runtime.Interpreter("3.12.10", "x86_64")

    def entry(self, *, version=(3, 12, 10), architecture="x86_64", variant=None):
        text = ".".join(map(str, version))
        return {
            "name": "cpython", "os": "windows", "libc": "none",
            "major": version[0], "minor": version[1], "patch": version[2], "prerelease": "",
            "arch": {"family": architecture, "variant": None}, "variant": variant,
            "build": "20250529",
            "url": f"https://github.com/astral-sh/python-build-standalone/releases/download/20250529/"
                   f"cpython-{text}%2B20250529-{architecture}-pc-windows-msvc-install_only_stripped.tar.gz",
            "sha256": "a" * 64,
        }

    def probe(self, **overrides):
        values = {
            "version": "3.12.10", "implementation": "cpython", "os": "win32",
            "platform": "win-amd64", "free_threaded": False, "debug": False,
            "prefix": str(self.project / ".venv"), "base_prefix": str(self.root / "python"),
        }
        values.update(overrides)
        return subprocess.CompletedProcess([], 0, json.dumps(values), "")

    def archive(self, members):
        path = self.root / "runtime.tar.gz"
        with tarfile.open(path, "w:gz") as bundle:
            for name, payload, kind in members:
                entry = tarfile.TarInfo(name)
                entry.type = kind
                entry.size = len(payload) if kind == tarfile.REGTYPE else 0
                if kind == tarfile.SYMTYPE:
                    entry.linkname = "../outside"
                bundle.addfile(entry, io.BytesIO(payload) if entry.size else None)
        return path

    def test_selects_project_venv_not_tool_interpreter(self):
        self.assertEqual(runtime.select_project_python(self.project), self.python)
        self.assertEqual(runtime.select_project_python(self.project, self.python), self.python)

    def test_missing_project_python_fails_without_global_fallback(self):
        self.python.unlink()
        with patch.object(runtime.sys, "prefix", str(self.root / "unrelated")):
            with self.assertRaisesRegex(WinappError, "not used as a fallback"):
                runtime.select_project_python(self.project)

    def test_missing_project_python_can_be_named_in_a_creation_hint(self):
        self.python.unlink()
        self.assertEqual(runtime.select_project_python(self.project, require_exists=False), self.python)

    def test_virtual_environment_is_checked_using_the_selected_interpreter(self):
        with patch.object(runtime.subprocess, "run", return_value=self.probe()):
            self.assertEqual(runtime.probe_interpreter(self.python, self.project, require_venv=True), self.info)
        with patch.object(runtime.subprocess, "run", return_value=self.probe(
            prefix=str(self.root / "python"), base_prefix=str(self.root / "python"),
        )):
            with self.assertRaisesRegex(WinappError, "requires a virtual environment"):
                runtime.probe_interpreter(self.python, self.project, require_venv=True)

    def test_invalid_virtual_environment_information_is_not_accepted(self):
        for prefix in (None, "", 12):
            with self.subTest(prefix=prefix):
                with patch.object(runtime.subprocess, "run", return_value=self.probe(prefix=prefix)):
                    with self.assertRaisesRegex(WinappError, "virtual environment information"):
                        runtime.probe_interpreter(self.python, self.project, require_venv=True)

    def test_probes_actual_version_and_isolates_probe_environment(self):
        with patch.object(runtime.subprocess, "run", return_value=self.probe()) as execute:
            self.assertEqual(runtime.probe_interpreter(self.python, self.project), self.info)
        self.assertEqual(execute.call_args.args[0][:3], [str(self.python), "-I", "-c"])
        self.assertEqual(execute.call_args.kwargs["cwd"], self.project)

    def test_architecture_and_abi_are_taken_from_project_python(self):
        with patch.object(runtime.subprocess, "run", return_value=self.probe(
            version="3.14.2", platform="win-arm64", free_threaded=True,
        )):
            actual = runtime.probe_interpreter(self.python, self.project)
        self.assertEqual(actual, runtime.Interpreter("3.14.2", "aarch64", True))

    def test_non_cpython_or_malformed_probe_is_rejected(self):
        for result in (self.probe(implementation="pypy"), self.probe(platform="unknown"),
                       self.probe(free_threaded="false"), self.probe(version="latest"),
                       subprocess.CompletedProcess([], 0, "[]", "")):
            with self.subTest(result=result.stdout):
                with patch.object(runtime.subprocess, "run", return_value=result):
                    with self.assertRaises(WinappError):
                        runtime.probe_interpreter(self.python, self.project)

    def test_broken_project_interpreter_surfaces_diagnostic(self):
        result = subprocess.CompletedProcess([], 103, "", "No Python at the configured path")
        with patch.object(runtime.subprocess, "run", return_value=result):
            with self.assertRaisesRegex(WinappError, "No Python"):
                runtime.probe_interpreter(self.python, self.project)

    def test_catalog_matches_exact_patch_not_latest(self):
        chosen = runtime.resolve_distribution(self.info, {
            "newer": self.entry(version=(3, 12, 15)),
            "exact": self.entry(),
        })
        self.assertEqual(chosen.interpreter, self.info)
        self.assertIn("3.12.10", chosen.url)

    def test_missing_exact_version_does_not_substitute(self):
        with self.assertRaisesRegex(WinappError, "No other Python version.*Upgrade microsoft-winappcli"):
            runtime.resolve_distribution(self.info, {"only": self.entry(version=(3, 12, 15))})

    def test_free_threaded_and_debug_variants_are_not_silently_changed(self):
        info = runtime.Interpreter("3.14.2", "aarch64", True, True)
        selected = runtime.resolve_distribution(info, {
            "standard": self.entry(version=(3, 14, 2), architecture="aarch64"),
            "requested": self.entry(version=(3, 14, 2), architecture="aarch64", variant="freethreaded+debug"),
        })
        self.assertEqual(selected.interpreter.variant, "freethreaded+debug")
        with self.assertRaises(WinappError):
            runtime.resolve_distribution(info, {"standard": self.entry(version=(3, 14, 2), architecture="aarch64")})

    def test_ambiguous_catalog_entries_are_not_guessed(self):
        with self.assertRaisesRegex(WinappError, "No unique"):
            runtime.resolve_distribution(self.info, {"a": self.entry(), "b": self.entry()})

    def test_download_origin_and_checksum_metadata_are_validated(self):
        for key, value in (("url", "https://example.com/python.tar.gz"), ("sha256", "missing"),
                           ("build", "../escape")):
            with self.subTest(key=key):
                entry = self.entry()
                entry[key] = value
                with self.assertRaises(WinappError):
                    runtime.resolve_distribution(self.info, {"x": entry})

    def test_recorded_distribution_is_reused_without_reading_live_catalog(self):
        distribution = runtime.resolve_distribution(self.info, {"x": self.entry()})
        with patch.object(runtime, "_read_catalog") as fetch:
            self.assertEqual(runtime.distribution_from_document(distribution.document(), self.info), distribution)
        fetch.assert_not_called()

    def test_download_checks_bytes_before_executing(self):
        payload = b"archive"
        distribution = runtime.Distribution(self.info, "20250529", self.entry()["url"],
                                            hashlib.sha256(payload).hexdigest())
        with patch.object(runtime, "urlopen", return_value=io.BytesIO(payload)):
            runtime._download(distribution, self.root / "download")
        self.assertEqual((self.root / "download").read_bytes(), payload)
        with patch.object(runtime, "urlopen", return_value=io.BytesIO(b"modified")):
            with self.assertRaisesRegex(WinappError, "SHA-256 mismatch"):
                runtime._download(distribution, self.root / "bad")

    def test_catalog_network_error_is_not_a_version_fallback(self):
        with patch.object(runtime, "urlopen", side_effect=OSError("network offline")) as fetch:
            with self.assertRaisesRegex(WinappError, "network offline"):
                runtime.resolve_distribution(self.info)
        fetch.assert_called_once()
        self.assertEqual(fetch.call_args.args[0].full_url, runtime.CATALOG_URL)

    def test_catalog_uses_the_wheel_commit_and_raw_bytes_hash_without_a_release_api(self):
        payload = json.dumps({"exact": self.entry()}, indent=2).encode() + b"\n"
        self.assertRegex(runtime.CATALOG_COMMIT, r"^[a-f0-9]{40}$")
        self.assertRegex(runtime.CATALOG_SHA256, r"^[a-f0-9]{64}$")
        with (
            patch.object(runtime, "CATALOG_SHA256", hashlib.sha256(payload).hexdigest()),
            patch.object(runtime, "MAX_CATALOG_BYTES", len(payload)),
            patch.object(runtime, "urlopen", return_value=io.BytesIO(payload)) as fetch,
        ):
            selected = runtime.resolve_distribution(self.info)
        self.assertEqual(selected.interpreter, self.info)
        fetch.assert_called_once()
        self.assertEqual(fetch.call_args.args[0].full_url,
                         f"https://raw.githubusercontent.com/astral-sh/uv/{runtime.CATALOG_COMMIT}/"
                         "crates/uv-python-managed/download-metadata.json")

    def test_modified_catalog_is_rejected_before_parsing_or_selecting_downloads(self):
        original = json.dumps({"exact": self.entry()}).encode()
        for payload in (original + b"\n", b"invalid json"):
            with self.subTest(payload=payload):
                with (
                    patch.object(runtime, "CATALOG_SHA256", hashlib.sha256(original).hexdigest()),
                    patch.object(runtime, "urlopen", return_value=io.BytesIO(payload)) as fetch,
                    patch.object(runtime.json, "loads") as parse,
                    self.assertRaisesRegex(WinappError, "catalog SHA-256 mismatch"),
                ):
                    runtime.resolve_distribution(self.info)
                fetch.assert_called_once()
                parse.assert_not_called()

    def test_checksum_verified_but_invalid_catalog_fails_explicitly(self):
        for payload in (b"[]", b"invalid json", b"\xff"):
            with self.subTest(payload=payload):
                with (
                    patch.object(runtime, "CATALOG_SHA256", hashlib.sha256(payload).hexdigest()),
                    patch.object(runtime, "urlopen", return_value=io.BytesIO(payload)),
                ):
                    with self.assertRaises(WinappError):
                        runtime.resolve_distribution(self.info)

    def test_catalog_size_limit_remains_enforced_before_parsing(self):
        with (
            patch.object(runtime, "MAX_CATALOG_BYTES", 4),
            patch.object(runtime, "urlopen", return_value=io.BytesIO(b'{"larger":true}')),
            patch.object(runtime.json, "loads") as parse,
            self.assertRaisesRegex(WinappError, "supported size"),
        ):
            runtime.resolve_distribution(self.info)
        parse.assert_not_called()

    def test_extracts_entire_runtime_and_preserves_license(self):
        archive = self.archive([
            ("python/python.exe", b"exe", tarfile.REGTYPE),
            ("python/Lib/os.py", b"stdlib", tarfile.REGTYPE),
            ("python/LICENSE.txt", b"license", tarfile.REGTYPE),
        ])
        target = self.root / "runtime"
        target.mkdir()
        runtime.extract_runtime(archive, target)
        self.assertEqual((target / "Lib" / "os.py").read_bytes(), b"stdlib")
        self.assertEqual((target / "LICENSE.txt").read_bytes(), b"license")

    def test_archive_cannot_escape_or_create_links(self):
        for name, kind in (("../outside", tarfile.REGTYPE), ("python/../../outside", tarfile.REGTYPE),
                           ("python/C:/outside", tarfile.REGTYPE), ("python/CON", tarfile.REGTYPE),
                           ("python/link", tarfile.SYMTYPE), ("python/pipe", tarfile.FIFOTYPE)):
            with self.subTest(name=name, kind=kind):
                archive = self.archive([(name, b"x", kind)])
                target = self.root / "runtime"
                target.mkdir(exist_ok=True)
                with self.assertRaisesRegex(WinappError, "Unsafe"):
                    runtime.extract_runtime(archive, target)
        self.assertFalse((self.root / "outside").exists())

    def test_expanded_size_is_bounded(self):
        archive = self.archive([("python/python.exe", b"large", tarfile.REGTYPE)])
        target = self.root / "runtime"
        target.mkdir()
        with patch.object(runtime, "MAX_EXTRACTED_BYTES", 2):
            with self.assertRaisesRegex(WinappError, "supported size"):
                runtime.extract_runtime(archive, target)
