// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Diagnostics.Telemetry;
using Microsoft.Diagnostics.Telemetry.Internal;
using System.Diagnostics.Tracing;
using WinApp.Cli.Services;

namespace WinApp.Cli.Telemetry.Events;

/// <summary>
/// Low-cardinality project classification for commands that operate on an app or workspace.
/// Values are derived only from recognized metadata markers and never contain project paths,
/// names, versions, dependency lists, repository identities, or source contents.
/// </summary>
[EventData]
internal sealed class ProjectContextEvent : EventBase
{
    internal ProjectContextEvent(string command, ProjectContext context)
    {
        Command = command;
        ProjectFamily = ToValue(context.Family);
        AppFramework = ToValue(context.Framework);
        TargetKind = ToValue(context.TargetKind);
        DetectionSource = ToValue(context.Source);
        Confidence = ToValue(context.Confidence);
        Packaging = ToValue(context.Packaging);
        ExecutionMode = ToValue(context.ExecutionMode);
    }

    public string Command { get; private set; }

    public string ProjectFamily { get; private set; }

    public string AppFramework { get; private set; }

    public string TargetKind { get; private set; }

    public string DetectionSource { get; private set; }

    public string Confidence { get; private set; }

    public string Packaging { get; private set; }

    public string ExecutionMode { get; private set; }

    public override PartA_PrivTags PartA_PrivTags => PrivTags.ProductAndServiceUsage;

    public override void ReplaceSensitiveStrings(Func<string, string> replaceSensitiveStrings)
    {
        Command = replaceSensitiveStrings(Command);
        ProjectFamily = replaceSensitiveStrings(ProjectFamily);
        AppFramework = replaceSensitiveStrings(AppFramework);
        TargetKind = replaceSensitiveStrings(TargetKind);
        DetectionSource = replaceSensitiveStrings(DetectionSource);
        Confidence = replaceSensitiveStrings(Confidence);
        Packaging = replaceSensitiveStrings(Packaging);
        ExecutionMode = replaceSensitiveStrings(ExecutionMode);
    }

    public static void Log(string command, ProjectContext context)
    {
        var telemetry = TelemetryFactory.Get<ITelemetry>();
        if (!telemetry.IsTelemetryOn)
        {
            return;
        }

        telemetry.Log(
            "ProjectContext_Event",
            LogLevel.Measure,
            new ProjectContextEvent(command, context),
            TelemetryCorrelation.CurrentId);
    }

    public static void Log(string command, Func<ProjectContext> createContext)
    {
        var telemetry = TelemetryFactory.Get<ITelemetry>();
        if (!telemetry.IsTelemetryOn)
        {
            return;
        }

        telemetry.Log(
            "ProjectContext_Event",
            LogLevel.Measure,
            new ProjectContextEvent(command, createContext()),
            TelemetryCorrelation.CurrentId);
    }

    private static string ToValue(WinApp.Cli.Services.ProjectFamily value) => value switch
    {
        WinApp.Cli.Services.ProjectFamily.Dotnet => "dotnet",
        WinApp.Cli.Services.ProjectFamily.Node => "node",
        WinApp.Cli.Services.ProjectFamily.Cpp => "cpp",
        WinApp.Cli.Services.ProjectFamily.Rust => "rust",
        WinApp.Cli.Services.ProjectFamily.Dart => "dart",
        WinApp.Cli.Services.ProjectFamily.Hybrid => "hybrid",
        WinApp.Cli.Services.ProjectFamily.Mixed => "mixed",
        _ => "unknown",
    };

    private static string ToValue(ProjectAppFramework value) => value switch
    {
        ProjectAppFramework.OtherDotnet => "other-dotnet",
        ProjectAppFramework.WinUI => "winui",
        ProjectAppFramework.Wpf => "wpf",
        ProjectAppFramework.WinForms => "winforms",
        ProjectAppFramework.Maui => "maui",
        ProjectAppFramework.Electron => "electron",
        ProjectAppFramework.Tauri => "tauri",
        ProjectAppFramework.Flutter => "flutter",
        ProjectAppFramework.ReactNativeWindows => "react-native-windows",
        ProjectAppFramework.Avalonia => "avalonia",
        ProjectAppFramework.Uwp => "uwp",
        ProjectAppFramework.WindowsAppSdk => "windows-app-sdk",
        ProjectAppFramework.Mixed => "mixed",
        _ => "unknown",
    };

    private static string ToValue(ProjectTargetKind value) => value switch
    {
        ProjectTargetKind.SourceProject => "source-project",
        ProjectTargetKind.Workspace => "workspace",
        ProjectTargetKind.BuildOutput => "build-output",
        ProjectTargetKind.Manifest => "manifest",
        _ => "unknown",
    };

    private static string ToValue(ProjectContextSource value) => value switch
    {
        ProjectContextSource.ExactMarker => "exact-marker",
        ProjectContextSource.AncestorMarker => "ancestor-marker",
        ProjectContextSource.ResolvedProject => "resolved-project",
        ProjectContextSource.SelectedProject => "selected-project",
        ProjectContextSource.NuGetMsBuild => "nuget-msbuild",
        _ => "none",
    };

    private static string ToValue(ProjectContextConfidence value) => value switch
    {
        ProjectContextConfidence.High => "high",
        ProjectContextConfidence.Medium => "medium",
        _ => "none",
    };

    private static string ToValue(ProjectContextPackaging value) => value switch
    {
        ProjectContextPackaging.Packaged => "packaged",
        ProjectContextPackaging.Sparse => "sparse",
        ProjectContextPackaging.Unpackaged => "unpackaged",
        _ => "unknown",
    };

    private static string ToValue(ProjectExecutionMode value) => value switch
    {
        ProjectExecutionMode.Project => "project",
        ProjectExecutionMode.Folder => "folder",
        _ => "none",
    };
}
