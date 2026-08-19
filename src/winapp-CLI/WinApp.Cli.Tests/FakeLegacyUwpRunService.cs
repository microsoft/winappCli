// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

internal sealed class FakeLegacyUwpRunService : ILegacyUwpRunService
{
    public bool IsLegacyUwp { get; set; }
    public LegacyUwpBuildOutcome? BuildOutcome { get; set; }
    public ProjectRunException? BuildThrows { get; set; }
    public List<FileInfo> DetectionCalls { get; } = [];
    public List<FileInfo> BuildCalls { get; } = [];
    public List<LegacyUwpRunOptions> BuildOptions { get; } = [];

    public bool IsLegacyUwpProject(FileInfo csproj)
    {
        DetectionCalls.Add(csproj);
        return IsLegacyUwp;
    }

    public Task<LegacyUwpBuildOutcome> BuildAndPrepareAsync(
        FileInfo csproj,
        LegacyUwpRunOptions options,
        CancellationToken cancellationToken)
    {
        BuildCalls.Add(csproj);
        BuildOptions.Add(options);
        if (BuildThrows is not null)
        {
            throw BuildThrows;
        }

        return Task.FromResult(
            BuildOutcome ?? throw new InvalidOperationException("FakeLegacyUwpRunService.BuildOutcome was not configured."));
    }
}
