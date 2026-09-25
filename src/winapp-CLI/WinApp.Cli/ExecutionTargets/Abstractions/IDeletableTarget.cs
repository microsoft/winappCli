// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.ExecutionTargets.Abstractions;

/// <summary>A backend whose managed target can be explicitly deleted without preparing it.</summary>
internal interface IDeletableTarget
{
    Task DeleteAsync(CancellationToken cancellationToken);
}
