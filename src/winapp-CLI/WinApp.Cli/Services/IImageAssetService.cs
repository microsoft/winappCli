// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;

namespace WinApp.Cli.Services;

internal interface IImageAssetService
{
    /// <summary>
    /// Generates MSIX image assets from a source image and saves them to the specified directory
    /// </summary>
    /// <param name="sourceImagePath">Path to the source image file</param>
    /// <param name="outputDirectory">Directory where generated assets will be saved</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task that completes when all assets are generated</returns>
    Task GenerateAssetsAsync(FileInfo sourceImagePath, DirectoryInfo outputDirectory, TaskContext taskContext, CancellationToken cancellationToken = default);
}
