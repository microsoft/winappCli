// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;

namespace WinApp.Cli.Services;

/// <summary>
/// Represents an asset reference extracted from an AppxManifest, including its path and dimensions.
/// </summary>
/// <param name="RelativePath">The relative path to the asset from the manifest directory (e.g., "Assets\StoreLogo.png")</param>
/// <param name="BaseWidth">The base width in pixels for the asset</param>
/// <param name="BaseHeight">The base height in pixels for the asset</param>
internal record ManifestAssetReference(string RelativePath, int BaseWidth, int BaseHeight);

internal interface IImageAssetService
{
    /// <summary>
    /// Generates MSIX image assets from a source image and saves them to the specified directory.
    /// Uses a hardcoded list of standard MSIX asset specifications.
    /// </summary>
    /// <param name="sourceImagePath">Path to the source image file</param>
    /// <param name="outputDirectory">Directory where generated assets will be saved</param>
    /// <param name="taskContext">Task context for status messages</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task that completes when all assets are generated</returns>
    Task GenerateAssetsAsync(FileInfo sourceImagePath, DirectoryInfo outputDirectory, TaskContext taskContext, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates MSIX image assets from a source image based on asset references from the manifest.
    /// Creates the base asset and scaled variants (scale-200, targetsize variants) matching the aspect ratio.
    /// </summary>
    /// <param name="sourceImagePath">Path to the source image file</param>
    /// <param name="manifestDirectory">Directory where the manifest is located (assets are relative to this)</param>
    /// <param name="assetReferences">Asset references extracted from the manifest</param>
    /// <param name="taskContext">Task context for status messages</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task that completes when all assets are generated</returns>
    Task GenerateAssetsFromManifestAsync(
        FileInfo sourceImagePath,
        DirectoryInfo manifestDirectory,
        IReadOnlyList<ManifestAssetReference> assetReferences,
        TaskContext taskContext,
        CancellationToken cancellationToken = default);
}
