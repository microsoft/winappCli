// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Records MSBuild invocations and replies with a canned result per call (build first, then evaluate),
/// so C++ project mode can be tested without Visual Studio.
/// </summary>
internal sealed class FakeMSBuildService : IMSBuildService
{
    public const string MSBuildPath = @"C:\VS\MSBuild\Current\Bin\MSBuild.exe";

    public List<IReadOnlyList<string>> Calls { get; } = [];

    /// <summary>Replies consumed in call order; the last one repeats.</summary>
    public List<(ProcessRunResult Result, string[] Lines)> Replies { get; } = [];

    public ProjectRunException? LocateFailure { get; set; }

    public Task<string> LocateCppMSBuildAsync(string architecture, CancellationToken cancellationToken)
        => LocateFailure is null ? Task.FromResult(MSBuildPath) : throw LocateFailure;

    public Task<ProcessRunResult> RunAsync(
        string msbuildPath,
        IReadOnlyList<string> arguments,
        Action<string>? onLine,
        CancellationToken cancellationToken)
    {
        Calls.Add(arguments);
        var (result, lines) = Replies.Count == 0
            ? (new ProcessRunResult(0, string.Empty, string.Empty), [])
            : Replies[Math.Min(Calls.Count, Replies.Count) - 1];
        foreach (var line in lines)
        {
            onLine?.Invoke(line);
        }

        return Task.FromResult(result);
    }
}
