// Copyright (c) Microsoft Corporation and Contributors.
// Licensed under the MIT License.

using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace winui_app;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        AppWindow.Resize(new SizeInt32(1280, 800));
        Bench.Start(this, ContentFrame, ChurnHost);
        // Show without activation so repeated launches do not take focus on a shared desktop.
        AppWindow.Show(false);
    }
}
