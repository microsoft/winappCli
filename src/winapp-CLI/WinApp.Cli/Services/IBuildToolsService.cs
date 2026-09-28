// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;
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
    /// <param name="taskContext">The task context for logging</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Full path to the executable</returns>
    /// <exception cref="FileNotFoundException">Tool not found even after installation</exception>
    /// <exception cref="InvalidOperationException">BuildTools installation failed</exception>
    Task<FileInfo> EnsureBuildToolAvailableAsync(string toolName, TaskContext taskContext, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies a build tool and keeps it in place so it can be launched safely. Callers start the
    /// returned tool from <see cref="VerifiedTool.Path"/> and dispose it once the tool has exited.
    /// Use this instead of starting a path from <see cref="EnsureBuildToolAvailableAsync"/>, which
    /// only reports a verdict and leaves the file free to be swapped before it runs.
    /// </summary>
    /// <exception cref="BuildToolSignatureException">
    /// The tool could not be held in place, or is not validly signed by Microsoft.
    /// </exception>
    VerifiedTool OpenVerifiedTool(FileInfo toolPath);

    Task<DirectoryInfo?> EnsureBuildToolsAsync(TaskContext taskContext, bool forceLatest = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Execute a build tool with the specified arguments
    /// </summary>
    /// <param name="tool">The tool to execute</param>
    /// <param name="arguments">Arguments to pass to the tool</param>
    /// <param name="taskContext">The task context for logging</param>
    /// <param name="printErrors">Whether to print errors using the tool's PrintErrorText method</param>
    /// <param name="toolPathOverride">Explicit executable path to run instead of resolving the tool by name (e.g. an architecture-matched signtool)</param>
    /// <param name="environment">Additional environment variables to set on the child process</param>
    /// <param name="workingDirectory">Working directory for the child process. When null, the process inherits the caller's current directory. Signing passes a trusted directory so a tool that shells out (e.g. the Trusted Signing dlib resolving <c>az</c>) cannot pick up an executable dropped into the caller's working directory.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Tuple containing (stdout, stderr)</returns>
    Task<(string stdout, string stderr)> RunBuildToolAsync(Tool tool, string arguments, TaskContext taskContext, bool printErrors = true, FileInfo? toolPathOverride = null, IReadOnlyDictionary<string, string>? environment = null, string? workingDirectory = null, CancellationToken cancellationToken = default);
}
