import os
from pathlib import Path
import sysconfig

from dynwinrt import (
    RoApartment,
    has_package_identity,
    init_winappsdk,
)
from winapp_bindings.microsoft.windows.app_lifecycle import AppInstance


def main() -> None:
    sdk = None
    if not has_package_identity():
        architecture = {
            "win-arm64": "arm64",
            "win-amd64": "x64",
        }.get(sysconfig.get_platform())
        if architecture is None:
            raise RuntimeError("Use Windows x64 or ARM64 CPython")
        bootstrap = (
            Path(__file__).resolve().parent
            / ".winapp"
            / "bin"
            / architecture
            / "Microsoft.WindowsAppRuntime.Bootstrap.dll"
        )
        if not bootstrap.is_file():
            raise FileNotFoundError(f"Run winapp-py restore; missing {bootstrap}")
        os.environ["WINAPPSDK_BOOTSTRAP_DLL_PATH"] = str(bootstrap)
        sdk = init_winappsdk(2, 5)
    try:
        with RoApartment():
            process_id = AppInstance.get_current().process_id
            if process_id != os.getpid():
                raise RuntimeError("Windows App SDK returned an unexpected process ID")
            print(f"Windows App SDK process ID: {process_id}")
    finally:
        del sdk


if __name__ == "__main__":
    main()
