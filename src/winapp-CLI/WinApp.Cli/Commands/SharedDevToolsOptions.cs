// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;

namespace WinApp.Cli.Commands;

internal static class SharedDevToolsOptions
{
    public static Option<string?> RootOption { get; } = new("--root")
    {
        Description = "Constrain execution to a live visual-tree root or subtree handle from DevTools inspect or Surface.list.",
    };

    public static Option<bool> AttachOption { get; } = new("--attach")
    {
        Description = "Authorize attaching DevTools if the target is not already attached.",
    };

    public static Option<bool> AllOption { get; } = new("--all")
    {
        Description = "Include framework and control-template elements, not just the ones your XAML declares.",
    };

    public static Option<string?> FilterOption { get; } = new("--filter")
    {
        Description = "Keep only elements whose type, x:Name, or source file matches this text (ancestors are kept for context).",
    };

    public static Option<string?> PropertyOption { get; } = new("--property", "-p")
    {
        Description = "Dependency property name (e.g. Width, IsEnabled, Background).",
    };

    public static Option<string?> TypeOption { get; } = new("--type")
    {
        Description = "XAML type to create the value as (e.g. Double, Boolean, String, Thickness). Inferred from the value when omitted.",
    };
}
