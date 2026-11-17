# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

from contextlib import contextmanager
from typing import Iterator


@contextmanager
def windows_runtime() -> Iterator[str]:
    from dynwinrt import RoApartment, has_package_identity, projected_lifetime_scope

    # WinUI's main thread is an STA; release projections before leaving it.
    with RoApartment(0), projected_lifetime_scope():
        import dynwinrt.auto_init

        mode = "Package identity" if has_package_identity() else "Bootstrap"
        yield f"{mode} - memory only"
