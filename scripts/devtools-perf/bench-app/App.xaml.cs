// Copyright (c) Microsoft Corporation and Contributors.
// Licensed under the MIT License.

using System;
using System.Diagnostics;
using Microsoft.UI.Xaml;

namespace winui_app;

public partial class App : Application
{
    internal static readonly DateTime ProcessStartUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime();
    internal static double SinceProcessStart() => (DateTime.UtcNow - ProcessStartUtc).TotalMilliseconds;
    internal static double ConstructedMs;
    internal static double LaunchedMs;

    private Window? _window;

    public App()
    {
        ConstructedMs = SinceProcessStart();
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        LaunchedMs = SinceProcessStart();
        _window = new MainWindow();
    }
}
