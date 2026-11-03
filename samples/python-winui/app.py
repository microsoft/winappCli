# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

import sys

from counter import Counter
from runtime import windows_runtime


def run() -> int:
    with windows_runtime() as mode:
        from window import run_window

        counter = Counter()
        print(f"Mode: {mode}", flush=True)
        print(f"Python: {sys.version.split()[0]} | {sys.executable}", flush=True)
        return run_window(counter, mode)


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
