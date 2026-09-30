// Copyright (c) Microsoft Corporation and Contributors.
// Licensed under the MIT License.

using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Foundation;
using Windows.Graphics;

namespace BindingOwnersFixture;

public sealed partial class MainWindow : Window
{
    public string HeadingText { get; }
    public Row[] Rows { get; }

    public MainWindow(string name, bool large = false)
    {
        HeadingText = name;
        Rows = [new Row(name + " row one"), new Row(name + " row two")];
        InitializeComponent();
        if (large)
        {
            var panel = (StackPanel)Content;
            for (var index = 0; index < 400; index++)
                panel.Children.Add(new TextBlock { Text = "Programmatic load row " + index });
        }
        Title = "Owned binding fixture " + name;
        AppWindow.Resize(new SizeInt32(520, 360));
    }

    public void ShowWithoutActivation()
    {
        AppWindow.Show(false);
        // Window-generated initialization normally runs on Activated, which this fixture avoids.
        Bindings.Initialize();
    }

    // Opens a windowed MenuFlyout whose item is centered near the given screen pixel.
    public void OpenMenuAt(int screenX, int screenY, Action clicked)
    {
        var origin = new NativePoint();
        ClientToScreen(WinRT.Interop.WindowNative.GetWindowHandle(this), ref origin);
        var scale = Content.XamlRoot.RasterizationScale;
        var item = new MenuFlyoutItem { Text = "Fixture menu item", Width = 120 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(item, "FixtureMenuItem");
        item.Click += (_, _) => clicked();
        var menu = new MenuFlyout();
        menu.Items.Add(item);
        menu.ShowAt(Content, new FlyoutShowOptions
        {
            Position = new Point((screenX - origin.X) / scale - 64, (screenY - origin.Y) / scale - 20),
        });
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window, ref NativePoint point);
}

public sealed class Row(string name)
{
    public string Name { get; } = name;
}
