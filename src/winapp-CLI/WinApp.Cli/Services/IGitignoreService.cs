// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;

namespace WinApp.Cli.Services;

internal interface IGitignoreService
{
    Task<bool> UpdateGitignoreAsync(DirectoryInfo projectDirectory, TaskContext taskContext, CancellationToken cancellationToken);
    bool AddCertificateToGitignore(DirectoryInfo projectDirectory, string certificateFileName, TaskContext taskContext);
}
