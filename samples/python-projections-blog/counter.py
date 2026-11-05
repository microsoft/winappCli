from collections.abc import Callable
import os
from pathlib import Path
import sysconfig

from dynwinrt import (
    RO_INIT_SINGLETHREADED,
    RoApartment,
    init_winappsdk,
    to_winrt_object,
)
from winapp_bindings.microsoft.ui.xaml import Application, Thickness, Window
from winapp_bindings.microsoft.ui.xaml.automation import AutomationProperties
from winapp_bindings.microsoft.ui.xaml.controls import Button, StackPanel, TextBlock
from winapp_bindings.windows.graphics import SizeInt32


def run_window() -> None:
    application: Application | None = None
    window: Window | None = None
    subscriptions: list[Callable[[], None]] = []

    with RoApartment(RO_INIT_SINGLETHREADED):

        def launched() -> None:
            nonlocal window
            count = 0
            label = TextBlock()
            label.text = "Clicks: 0"
            label.font_size = 28
            AutomationProperties.set_automation_id(label, "Count")

            button = Button()
            button.content = to_winrt_object("Click me")
            AutomationProperties.set_automation_id(button, "Increment")

            def clicked(_sender: object, _args: object) -> None:
                nonlocal count
                count += 1
                label.text = f"Clicks: {count}"
                print(label.text, flush=True)

            def closed(_sender: object, _args: object) -> None:
                if application is not None:
                    application.exit()

            panel = StackPanel()
            panel.spacing = 16
            panel.margin = Thickness(32, 32, 32, 32)
            panel.children.extend([label, button])

            window = Window()
            window.title = "Python WinUI counter"
            window.content = panel
            window.app_window.resize(SizeInt32(640, 440))
            subscriptions.append(button.subscribe_click(clicked))
            subscriptions.append(window.subscribe_closed(closed))
            window.activate()
            print("Python WinUI counter ready", flush=True)

        def initialize(_params: object) -> None:
            nonlocal application
            application = Application.create(launched)

        try:
            Application.start(initialize)
        finally:
            for unsubscribe in reversed(subscriptions):
                unsubscribe()
            window = None
            application = None


def main() -> None:
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
    runtime = init_winappsdk(2, 5)
    try:
        run_window()
    finally:
        del runtime


if __name__ == "__main__":
    main()
