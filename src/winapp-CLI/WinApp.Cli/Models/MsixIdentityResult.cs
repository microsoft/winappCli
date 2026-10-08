// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Models;

internal sealed record MsixIdentityResult(string PackageName, string Publisher, string ApplicationId)
{
    /// <summary>Set when the layout was given a <c>--unique-identity</c>; <see cref="PackageName"/> is then the derived name.</summary>
    public DevelopmentIdentity? Identity { get; init; }
}
