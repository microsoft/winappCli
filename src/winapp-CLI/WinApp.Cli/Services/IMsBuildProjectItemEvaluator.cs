// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

internal sealed record MsBuildProjectItem(
    string Kind,
    string Identity,
    string FullPath,
    string DefiningProjectFullPath,
    IReadOnlyDictionary<string, string> Metadata);

internal sealed record MsBuildProjectItemEvaluation(
    bool Succeeded,
    IReadOnlyList<MsBuildProjectItem> Items,
    IReadOnlyList<string> RestoredPackageDirectories,
    string? Error)
{
    internal static MsBuildProjectItemEvaluation Success(
        IReadOnlyList<MsBuildProjectItem> items,
        IReadOnlyList<string>? restoredPackageDirectories = null) =>
        new(true, items, restoredPackageDirectories ?? [], null);

    internal static MsBuildProjectItemEvaluation Failure(string error) =>
        new(false, [], [], error);
}

internal interface IMsBuildProjectItemEvaluator
{
    Task<MsBuildProjectItemEvaluation> EvaluateAsync(
        FileInfo project,
        CancellationToken cancellationToken = default);
}
