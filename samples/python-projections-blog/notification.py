import os
from pathlib import Path
import sysconfig

from dynwinrt import RoApartment, init_winappsdk
from winapp_bindings import AppNotificationBuilder, AppNotificationManager


def main() -> None:
    arch = {"win-amd64": "x64", "win-arm64": "arm64"}[sysconfig.get_platform()]
    os.environ["WINAPPSDK_BOOTSTRAP_DLL_PATH"] = str(
        Path(__file__).resolve().parent / f".winapp\\bin\\{arch}\\Microsoft.WindowsAppRuntime.Bootstrap.dll"
    )
    runtime = init_winappsdk(2, 5)
    try:
        with RoApartment():
            if not AppNotificationManager.is_supported():
                raise RuntimeError("Windows app notifications are not supported")

            manager = AppNotificationManager.get_default()
            manager.register()
            try:
                notification = (
                    AppNotificationBuilder()
                    .add_text("Hello from Python")
                    .add_text("This is a native Windows notification.")
                    .build_notification()
                )
                manager.show(notification)
                if notification.id == 0:
                    raise RuntimeError("Windows did not assign a notification ID")
            finally:
                manager.unregister()
    finally:
        del runtime


if __name__ == "__main__":
    main()
