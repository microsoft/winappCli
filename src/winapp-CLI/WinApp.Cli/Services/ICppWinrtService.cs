// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;

namespace WinApp.Cli.Services;

internal interface ICppWinrtService
{
    public FileInfo? FindCppWinrtExe(DirectoryInfo packagesDir, IDictionary<string, string> usedVersions);
    public Task RunWithRspAsync(FileInfo cppwinrtExe, IEnumerable<FileInfo> winmdInputs, DirectoryInfo outputDir, DirectoryInfo workingDirectory, TaskContext taskContext, CancellationToken cancellationToken = default);
}
