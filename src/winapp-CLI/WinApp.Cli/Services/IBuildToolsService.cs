// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Tools;

namespace WinApp.Cli.Services;

internal interface IBuildToolsService
{
    /// <summary>
    /// Get the path to a build tool if it exists in the current installation.
    /// This method does NOT install BuildTools if they are missing.
    /// Use EnsureBuildToolAvailableAsync if you want automatic installation.
    /// </summary>
    /// <param name="toolName">Name of the tool (e.g., 'mt.exe', 'signtool.exe')</param>
    /// <returns>Full path to the executable if found, null otherwise</returns>
    FileInfo? GetBuildToolPath(string toolName);

    /// <summary>
    /// Ensures a build tool is available by finding it or installing BuildTools if necessary.
    /// This method guarantees a tool path will be returned or an exception will be thrown.
    /// </summary>
    /// <param name="toolName">Name of the tool (e.g., 'mt.exe', 'signtool.exe')</param>  
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Full path to the executable</returns>
    /// <exception cref="FileNotFoundException">Tool not found even after installation</exception>
    /// <exception cref="InvalidOperationException">BuildTools installation failed</exception>
    Task<FileInfo> EnsureBuildToolAvailableAsync(string toolName, CancellationToken cancellationToken = default);

    Task<DirectoryInfo?> EnsureBuildToolsAsync(bool forceLatest = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Execute a build tool with the specified arguments
    /// </summary>
    /// <param name="tool">The tool to execute</param>
    /// <param name="arguments">Arguments to pass to the tool</param>
    /// <param name="printErrors">Whether to print errors using the tool's PrintErrorText method</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Tuple containing (stdout, stderr)</returns>
    Task<(string stdout, string stderr)> RunBuildToolAsync(Tool tool, string arguments, bool printErrors = true, CancellationToken cancellationToken = default);
}
