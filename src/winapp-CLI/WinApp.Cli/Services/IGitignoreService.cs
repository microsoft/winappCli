// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;

namespace WinApp.Cli.Services;

internal interface IGitignoreService
{
    Task<bool> AddWinAppFolderToGitIgnoreAsync(DirectoryInfo projectDirectory, TaskContext taskContext, CancellationToken cancellationToken);
    Task<bool> AddCertificateToGitignoreAsync(DirectoryInfo projectDirectory, string certificateFileName, TaskContext taskContext, CancellationToken cancellationToken);
}
