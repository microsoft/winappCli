// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Models;

internal sealed record MsixIdentityResult(string PackageName, string Publisher, string ApplicationId)
{
    public DevelopmentIdentity? Identity { get; init; }
}
