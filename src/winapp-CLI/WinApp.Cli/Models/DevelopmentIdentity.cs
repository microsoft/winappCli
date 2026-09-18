// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Models;

internal sealed record DevelopmentIdentityOptions(string OwnerPath, bool UniqueIdentity);

internal sealed record DevelopmentIdentity
{
    public required string Mode { get; init; }
    public required string OriginalPackageName { get; init; }
    public required string EffectivePackageName { get; init; }
    public required string Publisher { get; init; }
    public required string Version { get; init; }
    public required string Architecture { get; init; }
    public required string ResourceId { get; init; }
    public required string PackageFamilyName { get; init; }
    public required string ApplicationId { get; init; }
    public required string OwnerPath { get; init; }
    public required string LayoutPath { get; init; }
    public string? PackageFullName { get; init; }
    public long Revision { get; init; }
    public IReadOnlyDictionary<string, string> Aliases { get; init; } = new Dictionary<string, string>();
}
