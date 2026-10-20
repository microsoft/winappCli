# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

from contextlib import contextmanager
from dataclasses import dataclass
import os
import shutil
import sys
from threading import Event, Thread
from typing import Iterator, TextIO
import unicodedata

CHECK = "\u2705"
ERROR = "\u274c"
FRAMES = ("\u280b", "\u2819", "\u2839", "\u2838", "\u283c", "\u2834", "\u2826", "\u2827", "\u2807", "\u280f")
FRAME_INTERVAL = 0.08


def _width(text: str) -> int:
    return sum(
        0 if unicodedata.combining(character) else 2 if unicodedata.east_asian_width(character) in {"W", "F"} else 1
        for character in text
    )


def _fit(text: str, columns: int) -> str:
    width = max(0, columns - 1)
    if _width(text) <= width:
        return text
    fitted = ""
    for character in text:
        if _width(fitted + character) > max(0, width - 3):
            break
        fitted += character
    return fitted + "." * min(3, width)


class _Spinner:
    def __init__(self, stream: TextIO, text: str):
        self.stream = stream
        self.text = text
        self.stopped = Event()
        self.thread = Thread(target=self._animate, name="winapp-progress", daemon=True)
        self.width = 0
        self.error: OSError | UnicodeError | None = None

    def _draw(self, frame: str) -> None:
        columns = shutil.get_terminal_size().columns
        text = _fit(f"  {frame} {self.text}", columns)
        width = _width(text)
        erase_width = min(self.width, max(0, columns - 1))
        self.stream.write("\r" + text + " " * max(0, erase_width - width))
        self.stream.flush()
        self.width = width

    def _animate(self) -> None:
        frame = 1
        try:
            while not self.stopped.wait(FRAME_INTERVAL):
                self._draw(FRAMES[frame % len(FRAMES)])
                frame += 1
        except (OSError, UnicodeError) as error:
            self.error = error

    def start(self) -> None:
        self._draw(FRAMES[0])
        self.thread.start()

    def stop(self) -> None:
        self.stopped.set()
        if self.thread.ident is not None:
            self.thread.join()
        erase_width = min(self.width, max(0, shutil.get_terminal_size().columns - 1))
        self.stream.write("\r" + " " * erase_width + "\r")
        self.stream.flush()


@dataclass
class Step:
    label: str

    def complete(self, label: str) -> None:
        self.label = label


class Workflow:
    def __init__(self, title: str, *, verbose: bool = False):
        self.title = title
        self.verbose = verbose
        self.message: str | None = None

    def __enter__(self) -> "Workflow":
        print(self.title, flush=True)
        return self

    def __exit__(self, exception_type, exception, traceback) -> None:
        if exception_type is None and self.message is not None:
            print(f"{CHECK} {self.message}", flush=True)

    def succeed(self, message: str) -> None:
        self.message = message

    @contextmanager
    def step(self, pending: str, completed: str | None = None) -> Iterator[Step]:
        step = Step(completed or pending)
        stream = sys.stdout
        animate = (
            not self.verbose and sys.stdout.isatty()
            and os.environ.get("TERM") != "dumb" and "NO_COLOR" not in os.environ
            and _width(f"  {FRAMES[0]} {pending}") < shutil.get_terminal_size().columns
        )
        spinner = _Spinner(stream, pending) if animate else None
        try:
            try:
                if spinner is not None:
                    spinner.start()
                else:
                    print(f"  ... {pending}", file=stream, flush=True)
                yield step
            finally:
                if spinner is not None:
                    spinner.stop()
            if spinner is not None and spinner.error is not None:
                raise spinner.error
        except BaseException:
            print(f"  {ERROR} {pending}", file=stream, flush=True)
            raise
        else:
            print(f"  {CHECK} {step.label}", file=stream, flush=True)
