// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

internal sealed class FakeMsBuildProjectItemEvaluator
    : IMsBuildProjectItemEvaluator
{
    public Func<FileInfo, CancellationToken, Task<MsBuildProjectItemEvaluation>>?
        Handler { get; set; }

    public List<string> EvaluatedProjects { get; } = [];

    public Task<MsBuildProjectItemEvaluation> EvaluateAsync(
        FileInfo project,
        CancellationToken cancellationToken = default)
    {
        lock (EvaluatedProjects)
        {
            EvaluatedProjects.Add(project.FullName);
        }
        return Handler?.Invoke(project, cancellationToken)
            ?? Task.FromResult(
                MsBuildProjectItemEvaluation.Success([]));
    }
}
