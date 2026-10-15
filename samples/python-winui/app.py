# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

from pathlib import Path
import sys

from counter import Counter
from runtime import windows_runtime


ROOT = Path(__file__).resolve().parent


def run() -> int:
    from window import run_window

    with windows_runtime(ROOT):
        counter = Counter()
        print("Mode: Bootstrap - memory only", flush=True)
        print(f"Python: {sys.version.split()[0]} | {sys.executable}", flush=True)
        return run_window(counter)


def main() -> int:
    try:
        return run()
    except (ImportError, OSError, ValueError, RuntimeError) as error:
        print(f"error: {error}\nSee the Python guide for initialization and runtime prerequisites.", file=sys.stderr)
        return 1
    except KeyboardInterrupt:
        return 130


if __name__ == "__main__":
    sys.exit(main())
