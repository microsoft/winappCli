// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Services;
using WinApp.Cli.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace WinApp.Cli.Tests;

/// <summary>
/// Configurable fake <see cref="IBuildToolsService"/> that records every
/// <see cref="RunBuildToolAsync"/> invocation and lets a test dictate the outcome
/// (return value or thrown exception). Used to exercise services that shell out to
/// SDK tools (signtool, makeappx, …) without downloading or running the real tools.
/// Distinct from the makeappx-focused fake in the MsixService tests: this one supports
/// throwing handlers so error paths can be covered.
/// </summary>
internal sealed class ConfigurableBuildToolsService : IBuildToolsService
{
    public List<(string Tool, string Arguments)> Invocations { get; } = [];

    /// <summary>Handler invoked for each RunBuildToolAsync call. Return stdout/stderr or throw.</summary>
    public Func<Tool, string, TaskContext, (string Stdout, string Stderr)>? RunBuildToolHandler { get; set; }

    public Func<string, FileInfo?>? GetBuildToolPathHandler { get; set; }
    public Func<string, FileInfo>? EnsureBuildToolAvailableHandler { get; set; }

    public FileInfo? GetBuildToolPath(string toolName)
        => GetBuildToolPathHandler?.Invoke(toolName);

    public VerifiedTool OpenVerifiedTool(FileInfo toolPath)
        => VerifiedTool.Open(toolPath, static (_, _) => true, NullLogger.Instance);

    public Task<FileInfo> EnsureBuildToolAvailableAsync(string toolName, TaskContext taskContext, CancellationToken cancellationToken = default)
    {
        var result = EnsureBuildToolAvailableHandler?.Invoke(toolName)
            ?? throw new FileNotFoundException(toolName);
        return Task.FromResult(result);
    }

    public Task<DirectoryInfo?> EnsureBuildToolsAsync(TaskContext taskContext, bool forceLatest = false, CancellationToken cancellationToken = default)
        => Task.FromResult<DirectoryInfo?>(null);

    public Task<(string stdout, string stderr)> RunBuildToolAsync(Tool tool, string arguments, TaskContext taskContext, bool printErrors = true, FileInfo? toolPathOverride = null, IReadOnlyDictionary<string, string>? environment = null, string? workingDirectory = null, CancellationToken cancellationToken = default)
    {
        Invocations.Add((tool.ExecutableName, arguments));

        if (RunBuildToolHandler is not null)
        {
            var (stdout, stderr) = RunBuildToolHandler(tool, arguments, taskContext);
            return Task.FromResult((stdout, stderr));
        }

        return Task.FromResult((string.Empty, string.Empty));
    }
}
