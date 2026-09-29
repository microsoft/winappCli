// Copyright (c) Microsoft Corporation and Contributors.
// Licensed under the MIT License.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
}

public sealed class Row(string name)
{
    public string Name { get; } = name;
}
