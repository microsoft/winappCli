// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

internal enum ProjectFamily
{
    Unknown,
    Dotnet,
    Node,
    Cpp,
    Rust,
    Dart,
    Hybrid,
    Mixed,
}

internal enum ProjectAppFramework
{
    Unknown,
    OtherDotnet,
    WinUI,
    Wpf,
    WinForms,
    Maui,
    Electron,
    Tauri,
    Flutter,
    ReactNativeWindows,
    Avalonia,
    Uwp,
    WindowsAppSdk,
    Mixed,
}

internal enum ProjectTargetKind
{
    Unknown,
    SourceProject,
    Workspace,
    BuildOutput,
    Manifest,
}

internal enum ProjectContextSource
{
    None,
    ExactMarker,
    AncestorMarker,
    ResolvedProject,
    SelectedProject,
    NuGetMsBuild,
}

internal enum ProjectContextConfidence
{
    None,
    Medium,
    High,
}

internal enum ProjectContextPackaging
{
    Unknown,
    Packaged,
    Sparse,
    Unpackaged,
}

internal enum ProjectExecutionMode
{
    None,
    Project,
    Folder,
}

internal sealed record ProjectContext(
    ProjectFamily Family,
    ProjectAppFramework Framework,
    ProjectTargetKind TargetKind,
    ProjectContextSource Source,
    ProjectContextConfidence Confidence,
    ProjectContextPackaging Packaging = ProjectContextPackaging.Unknown,
    ProjectExecutionMode ExecutionMode = ProjectExecutionMode.None)
{
    public bool IsKnown => Family != ProjectFamily.Unknown || Framework != ProjectAppFramework.Unknown;

    public static ProjectContext Unknown(
        ProjectTargetKind targetKind = ProjectTargetKind.Unknown,
        ProjectExecutionMode executionMode = ProjectExecutionMode.None) =>
        new(
            ProjectFamily.Unknown,
            ProjectAppFramework.Unknown,
            targetKind,
            ProjectContextSource.None,
            ProjectContextConfidence.None,
            ProjectContextPackaging.Unknown,
            executionMode);
}
