# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

from contextlib import contextmanager
import os
from pathlib import Path
from typing import Iterator


@contextmanager
def windows_runtime(project: Path) -> Iterator[None]:
    from dynwinrt import RoApartment, init_winappsdk, projected_lifetime_scope
    from winapp_bindings._winapp_runtime import WINDOWS_APP_SDK_VERSION, bootstrap_dll_path

    dll = bootstrap_dll_path(project)
    os.environ["WINAPPSDK_BOOTSTRAP_DLL_PATH"] = str(dll)
    bootstrap = init_winappsdk(*WINDOWS_APP_SDK_VERSION)
    print(f"Bootstrap: {dll}", flush=True)
    try:
        # WinUI's main thread is an STA; release projections before leaving it.
        with RoApartment(0), projected_lifetime_scope():
            yield
    finally:
        del bootstrap
