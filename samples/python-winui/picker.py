# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

from pathlib import Path
import sys
from threading import Thread

from dynwinrt import RoApartment, projected_lifetime_scope, to_winrt_object
from winapp_bindings.microsoft.ui.windowing import OverlappedPresenter
from winapp_bindings.microsoft.ui.xaml import (
    Application, ApplicationTheme, GridLength, GridUnitType,
    HorizontalAlignment, Style, TextWrapping, Thickness, Window,
)
from winapp_bindings.microsoft.ui.xaml.automation import AutomationProperties
from winapp_bindings.microsoft.ui.xaml.controls import (
    Button, Grid, RowDefinition, ScrollBarVisibility, ScrollViewer, TextBlock,
)
from winapp_bindings.microsoft.ui.xaml.media import SolidColorBrush
from winapp_bindings.microsoft.windows.storage.pickers import FileOpenPicker
from winapp_bindings.windows.graphics import SizeInt32
from winapp_bindings.windows.ui import ColorHelper

from runtime import windows_runtime


def run() -> int:
    application = None
    window = None
    subscriptions = []
    failed = False
    original_hook = sys.unraisablehook

    def launched():
        nonlocal window
        window = Window()
        window.title = "Python WinUI file picker"
        panel = Grid()
        panel.row_spacing = 16
        panel.margin = Thickness(24, 24, 24, 24)
        button_row = RowDefinition()
        button_row.height = GridLength(1, GridUnitType.Auto)
        result_row = RowDefinition()
        result_row.height = GridLength(1, GridUnitType.Star)
        panel.row_definitions.extend([button_row, result_row])
        button = Button()
        button.horizontal_alignment = HorizontalAlignment.Left
        accent = application.resources.lookup(to_winrt_object("AccentButtonStyle"))
        if accent is None:
            raise RuntimeError("WinUI AccentButtonStyle was not loaded.")
        button.style = Style(accent)
        button.content = to_winrt_object("Pick an image")
        status = TextBlock()
        status.text = "Choose a PNG or JPEG image."
        status.text_wrapping = TextWrapping.Wrap
        status.is_text_selection_enabled = True
        scroll = ScrollViewer()
        scroll.horizontal_scroll_bar_visibility = ScrollBarVisibility.Disabled
        scroll.vertical_scroll_bar_visibility = ScrollBarVisibility.Auto
        scroll.content = status
        Grid.set_row(scroll, 1)
        AutomationProperties.set_automation_id(button, "Pick")
        AutomationProperties.set_automation_id(status, "Result")
        panel.children.extend([button, scroll])
        shade = 32 if application.requested_theme == ApplicationTheme.Dark else 243
        root = Grid()
        root.background = SolidColorBrush(ColorHelper.from_argb(255, shade, shade, shade))
        root.children.append(panel)
        window.content = root
        app_window = window.app_window
        presenter = OverlappedPresenter.create()
        presenter.is_resizable = False
        presenter.is_maximizable = False
        app_window.set_presenter(presenter)
        app_window.resize(SizeInt32(620, 420))
        dispatcher = window.dispatcher_queue

        def choose(_sender, _args):
            button.is_enabled = False
            window_id = window.app_window.id

            def wait_for_result():
                with RoApartment(1), projected_lifetime_scope():
                    try:
                        picker = FileOpenPicker.create_instance(window_id)
                        picker.file_type_filter.extend([".png", ".jpg", ".jpeg"])
                        result = picker.pick_single_file_async().wait()
                        text = result.path if result else "Cancelled"
                    except (OSError, RuntimeError) as error:
                        text = f"Picker failed: {error}"
                        print(text, file=sys.stderr, flush=True)

                    def update():
                        status.text = text
                        button.is_enabled = True
                        print(text, flush=True)

                    if not dispatcher.try_enqueue(update):
                        print("Picker window closed before the result could be displayed.",
                              file=sys.stderr, flush=True)

            Thread(target=wait_for_result).start()

        subscriptions.append(button.subscribe_click(choose))
        subscriptions.append(window.subscribe_closed(lambda _s, _a: Application.get_current().exit()))
        window.activate()

    def initialize(_params):
        nonlocal application
        application = Application.create(launched)

    def callback_failed(error):
        nonlocal failed
        failed = True
        original_hook(error)
        current = Application.get_current()
        if current is not None:
            current.exit()

    with windows_runtime(Path(__file__).resolve().parent):
        try:
            sys.unraisablehook = callback_failed
            Application.start(initialize)
        finally:
            sys.unraisablehook = original_hook
            for unsubscribe in reversed(subscriptions):
                unsubscribe()
            window = None
            application = None
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(run())
