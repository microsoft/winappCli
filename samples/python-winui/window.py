# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

from collections.abc import Callable
import sys

from winapp_bindings.microsoft.ui.xaml import (
    Application,
    ApplicationTheme,
    HorizontalAlignment,
    TextAlignment,
    TextWrapping,
    Thickness,
    VerticalAlignment,
    Window,
)
from winapp_bindings.microsoft.ui.xaml.automation import AutomationProperties
from winapp_bindings.microsoft.ui.xaml.controls import (
    Button,
    Grid,
    Orientation,
    ScrollBarVisibility,
    ScrollViewer,
    StackPanel,
    TextBlock,
)
from winapp_bindings.microsoft.ui.xaml.media import FontFamily, SolidColorBrush
from winapp_bindings.windows.graphics import SizeInt32
from winapp_bindings.windows.ui import ColorHelper
from winapp_bindings.windows.ui.text import FontWeight

from counter import Counter


def text_block(
    text: str,
    size: float = 14,
    *,
    weight: int = 400,
    automation_id: str | None = None,
) -> TextBlock:
    block = TextBlock()
    block.text = text
    block.font_size = size
    block.font_weight = FontWeight(weight)
    block.text_alignment = TextAlignment.Center
    block.text_wrapping = TextWrapping.Wrap
    if automation_id is not None:
        AutomationProperties.set_automation_id(block, automation_id)
    return block


def step_button(symbol: str, name: str, automation_id: str) -> Button:
    button = Button()
    button.content = text_block(symbol, 28, weight=600)
    button.width = 56
    button.height = 56
    button.padding = Thickness(0, 0, 0, 0)
    button.vertical_alignment = VerticalAlignment.Center
    AutomationProperties.set_automation_id(button, automation_id)
    AutomationProperties.set_name(button, name)
    return button


class CounterWindow:
    def __init__(self, counter: Counter) -> None:
        self.counter = counter
        self._subscriptions: list[Callable[[], None]] = []

        self.count_text = text_block(str(counter.value), 48, weight=600, automation_id="Count")
        self.count_text.font_family = FontFamily("Consolas")
        self.count_text.text_wrapping = TextWrapping.NoWrap
        self.count_text.width = 300
        self.count_text.height = 72
        self.count_text.padding = Thickness(0, 8, 0, 8)
        self.count_text.vertical_alignment = VerticalAlignment.Center
        self.status_text = text_block(
            "Closing the window resets the counter.",
            automation_id="Status",
        )
        self.increment_button = step_button("+", "Add one", "Increment")
        self.decrement_button = step_button("-", "Subtract one", "Decrement")
        self.decrement_button.is_enabled = counter.value > 0

        content = self._build_content()
        self.window = Window()
        self.window.title = "Python WinUI counter"
        self.window.content = content
        app_window = self.window.app_window
        if app_window is None:
            raise RuntimeError("WinUI returned no AppWindow.")
        app_window.resize(SizeInt32(760, 620))

    def _build_content(self) -> Grid:
        title = text_block("Python + WinUI 3", 26, weight=600)
        mode = text_block(
            "Bootstrap - memory only",
            automation_id="Mode",
        )

        controls = StackPanel()
        controls.orientation = Orientation.Horizontal
        controls.spacing = 12
        controls.horizontal_alignment = HorizontalAlignment.Center
        control_children = controls.children
        if control_children is None:
            raise RuntimeError("WinUI returned no counter controls.")
        for child in (self.increment_button, self.count_text, self.decrement_button):
            control_children.append(child)

        panel = StackPanel()
        panel.max_width = 500
        panel.margin = Thickness(24, 32, 24, 32)
        panel.spacing = 20
        panel.horizontal_alignment = HorizontalAlignment.Center
        panel.vertical_alignment = VerticalAlignment.Center
        children = panel.children
        if children is None:
            raise RuntimeError("WinUI returned no StackPanel children.")
        for child in (title, mode, controls, self.status_text):
            children.append(child)

        scroll = ScrollViewer()
        scroll.horizontal_scroll_bar_visibility = ScrollBarVisibility.Disabled
        scroll.content = panel

        application = Application.get_current()
        if application is None:
            raise RuntimeError("WinUI returned no current Application.")
        shade = 32 if application.requested_theme == ApplicationTheme.Dark else 243
        content = Grid()
        content.background = SolidColorBrush(ColorHelper.from_argb(255, shade, shade, shade))
        root_children = content.children
        if root_children is None:
            raise RuntimeError("WinUI returned no Grid children.")
        root_children.append(scroll)
        return content

    def show(self) -> None:
        self._subscriptions.append(self.increment_button.subscribe_click(self._on_increment))
        self._subscriptions.append(self.decrement_button.subscribe_click(self._on_decrement))
        self._subscriptions.append(self.window.subscribe_closed(self._on_closed))
        self.window.activate()

    def _on_increment(self, _sender: object, _args: object) -> None:
        self._change_count(self.counter.increment)

    def _on_decrement(self, _sender: object, _args: object) -> None:
        self._change_count(self.counter.decrement)

    def _change_count(self, change: Callable[[], None]) -> None:
        change()
        self.count_text.text = str(self.counter.value)
        self.decrement_button.is_enabled = self.counter.value > 0
        self.status_text.text = "Memory only - not saved."

    def _on_closed(self, _sender: object, _args: object) -> None:
        current = Application.get_current()
        if current is not None:
            current.exit()

    def dispose(self) -> None:
        for unsubscribe in reversed(self._subscriptions):
            unsubscribe()
        self._subscriptions.clear()


def run_window(counter: Counter) -> int:
    application: Application | None = None
    window: CounterWindow | None = None
    failed = False
    original_hook = sys.unraisablehook

    def launched() -> None:
        nonlocal window
        window = CounterWindow(counter)
        window.show()

    def initialize(_params: object) -> None:
        nonlocal application
        application = Application.create(launched)

    def callback_failed(error) -> None:
        nonlocal failed
        failed = True
        original_hook(error)
        current = Application.get_current()
        if current is not None:
            current.exit()

    try:
        sys.unraisablehook = callback_failed
        Application.start(initialize)
    finally:
        sys.unraisablehook = original_hook
        if window is not None:
            window.dispose()
        window = None
        application = None
    return 1 if failed else 0
