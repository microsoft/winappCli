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

    // Opens the app's windowed MenuFlyout with its item centered near the given screen pixel.
    public void OpenMenuAt(int screenX, int screenY, Action clicked)
    {
        var origin = new NativePoint();
        ClientToScreen(WinRT.Interop.WindowNative.GetWindowHandle(this), ref origin);
        var scale = Content.XamlRoot.RasterizationScale;
        var menu = (MenuFlyout)((FrameworkElement)Content).Resources["FixtureMenu"];
        _menuClicked = clicked;
        if (!_menuWired)
        {
            ((MenuFlyoutItem)menu.Items[0]).Click += (_, _) => _menuClicked?.Invoke();
            _menuWired = true;
        }
        menu.ShowAt(Content, new FlyoutShowOptions
        {
            Position = new Point((screenX - origin.X) / scale - 64, (screenY - origin.Y) / scale - 20),
        });
    }

    private Action? _menuClicked;
    private bool _menuWired;

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window, ref NativePoint point);
}

public sealed class Row(string name)
{
    public string Name { get; } = name;
}
