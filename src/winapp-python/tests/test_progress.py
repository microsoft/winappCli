# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

from contextlib import redirect_stdout
import io
import os
import threading
import time
from unittest.mock import patch
import unittest

from winapp.errors import WinappError
from winapp.progress import CHECK, ERROR, FRAMES, Workflow


class TerminalOutput(io.StringIO):
    def __init__(self):
        super().__init__()
        self.advanced = threading.Event()
        self.writes: list[str] = []

    def isatty(self):
        return True

    def write(self, text):
        self.writes.append(text)
        if any(frame in text for frame in FRAMES[1:]):
            self.advanced.set()
        return super().write(text)


class ProgressTests(unittest.TestCase):
    def test_redirected_output_is_plain_grouped_and_reports_success_last(self):
        output = io.StringIO()
        with redirect_stdout(output):
            with Workflow("Preparing Python bindings") as progress:
                with progress.step("Generate bindings") as step:
                    step.complete("Bindings current: winapp_bindings")
                with progress.step("Install dependencies", "Dependencies installed"):
                    pass
                progress.succeed("Python bindings ready.")
        text = output.getvalue()
        self.assertNotIn("\x1b", text)
        self.assertIn(f"{CHECK} Bindings current: winapp_bindings", text)
        self.assertTrue(text.endswith(f"{CHECK} Python bindings ready.\n"))
        self.assertNotIn("\r", text)
        self.assertFalse(any(frame in text for frame in FRAMES))

    def test_failed_step_does_not_report_completion_or_overall_readiness(self):
        output = io.StringIO()
        with redirect_stdout(output), self.assertRaisesRegex(WinappError, "real failure"):
            with Workflow("Preparing Python bindings") as progress:
                progress.succeed("Python bindings ready.")
                with progress.step("Install dependencies", "Dependencies installed"):
                    raise WinappError("real failure")
        text = output.getvalue()
        self.assertIn(f"{ERROR} Install dependencies", text)
        self.assertNotIn("Dependencies installed", text)
        self.assertNotIn("Python bindings ready.", text)

    def test_tty_animations_update_while_waiting_and_stop_before_completion(self):
        output = TerminalOutput()
        with (
            patch.dict(os.environ, {"TERM": "xterm"}),
            patch("winapp.progress.shutil.get_terminal_size", return_value=os.terminal_size((80, 24))),
            redirect_stdout(output),
        ):
            os.environ.pop("NO_COLOR", None)
            with Workflow("Preparing Python bindings") as progress:
                with progress.step("Generate bindings", "Bindings generated: winapp_bindings"):
                    self.assertTrue(output.advanced.wait(2), "Spinner did not advance during the step.")
                self.assertFalse(any(thread.name == "winapp-progress" for thread in threading.enumerate()))
                progress.succeed("Python bindings ready.")
        text = output.getvalue()
        self.assertIn("\r", text)
        self.assertIn(f"{CHECK} Bindings generated: winapp_bindings", text)
        settled = output.getvalue()
        time.sleep(0.1)
        self.assertEqual(output.getvalue(), settled)

    def test_animation_stops_on_failure_and_keyboard_interrupt(self):
        for failure in (WinappError("real failure"), KeyboardInterrupt()):
            with self.subTest(failure=type(failure).__name__):
                output = TerminalOutput()
                with (
                    patch.dict(os.environ, {"TERM": "xterm"}),
                    patch("winapp.progress.shutil.get_terminal_size", return_value=os.terminal_size((80, 24))),
                    redirect_stdout(output),
                    self.assertRaises(type(failure)),
                ):
                    os.environ.pop("NO_COLOR", None)
                    with Workflow("Preparing Python bindings") as progress:
                        with progress.step("Install dependencies", "Dependencies installed"):
                            self.assertTrue(output.advanced.wait(2))
                            raise failure
                self.assertFalse(any(thread.name == "winapp-progress" for thread in threading.enumerate()))
                self.assertIn(f"{ERROR} Install dependencies", output.getvalue())
                self.assertNotIn(f"{CHECK} Dependencies installed", output.getvalue())

    def test_verbose_and_narrow_terminals_do_not_rewrite_output(self):
        for verbose, columns, settings in (
            (True, 80, {}), (False, 10, {}), (False, 80, {"TERM": "dumb"}),
            (False, 80, {"NO_COLOR": "1"}),
        ):
            with self.subTest(verbose=verbose, columns=columns, settings=settings):
                output = io.StringIO()
                with (
                    patch.object(output, "isatty", return_value=True),
                    patch.dict(os.environ, settings),
                    patch("winapp.progress.shutil.get_terminal_size", return_value=os.terminal_size((columns, 24))),
                    redirect_stdout(output),
                ):
                    with Workflow("Preparing Python bindings", verbose=verbose) as progress:
                        with progress.step("Generate bindings"):
                            print("Detailed output", flush=True)
                self.assertNotIn("\x1b", output.getvalue())
                self.assertNotIn("\r", output.getvalue())
                self.assertFalse(any(frame in output.getvalue() for frame in FRAMES))

    def test_unicode_width_and_terminal_resizing_do_not_wrap_the_wait_line(self):
        output = TerminalOutput()
        columns = 30
        with (
            patch.dict(os.environ, {"TERM": "xterm"}),
            patch("winapp.progress.shutil.get_terminal_size",
                  side_effect=lambda: os.terminal_size((columns, 24))),
            redirect_stdout(output),
        ):
            os.environ.pop("NO_COLOR", None)
            with Workflow("Preparing Python bindings") as progress:
                with progress.step("Install \u4f8b\u5b50 dependencies", "Dependencies installed"):
                    columns = 16
                    self.assertTrue(output.advanced.wait(2))
        frames = [write for write in output.writes if any(frame in write for frame in FRAMES[1:])]
        self.assertTrue(frames)
        self.assertTrue(all(len(write.strip("\r ")) <= 15 for write in frames))

    def test_animation_output_failure_is_not_reported_as_success(self):
        class FailingTerminal(TerminalOutput):
            def write(self, text):
                if any(frame in text for frame in FRAMES[1:]):
                    self.advanced.set()
                    raise OSError("Terminal output failed")
                return super().write(text)

        output = FailingTerminal()
        with (
            patch.dict(os.environ, {"TERM": "xterm"}),
            patch("winapp.progress.shutil.get_terminal_size", return_value=os.terminal_size((80, 24))),
            redirect_stdout(output),
            self.assertRaisesRegex(OSError, "Terminal output failed"),
        ):
            os.environ.pop("NO_COLOR", None)
            with Workflow("Preparing Python bindings") as progress:
                with progress.step("Install dependencies", "Dependencies installed"):
                    self.assertTrue(output.advanced.wait(2))
        self.assertIn(f"{ERROR} Install dependencies", output.getvalue())
        self.assertNotIn(f"{CHECK} Dependencies installed", output.getvalue())
        self.assertFalse(any(thread.name == "winapp-progress" for thread in threading.enumerate()))
