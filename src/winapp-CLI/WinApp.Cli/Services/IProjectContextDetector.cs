// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

internal interface IProjectContextDetector
{
    ProjectContext DetectProject(FileInfo projectFile);

    ProjectContext DetectDirectory(
        DirectoryInfo directory,
        ProjectTargetKind fallbackTargetKind = ProjectTargetKind.Workspace);

    ProjectContext DetectDirectories(
        IEnumerable<DirectoryInfo> directories,
        ProjectTargetKind fallbackTargetKind);

    ProjectContext CreateNuGetContext(string? frameworkHint);
}
