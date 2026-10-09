// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Models;

/// <summary>Which identity a run registers, and the path a unique identity is derived from.</summary>
internal sealed record DevelopmentIdentityOptions(string OwnerPath, bool UniqueIdentity);

/// <summary>The identity a <c>--unique-identity</c> run registered, reported as <c>Identity</c> in <c>run --json</c>.</summary>
internal sealed record DevelopmentIdentity
{
    public required string OriginalPackageName { get; init; }
    public required string PackageName { get; init; }
    public required string PackageFamilyName { get; init; }
    public required string OwnerPath { get; init; }

    /// <summary>Authored execution aliases, mapped from their original to their renamed form.</summary>
    public IReadOnlyDictionary<string, string> Aliases { get; init; } = new Dictionary<string, string>();
}
