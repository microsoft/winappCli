// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Helpers;

namespace WinApp.Cli.Models;

internal sealed record InspectorAliasRequest(string? ApplicationId = null);

internal sealed record InspectorAlias(
    string? AliasName,
    ExecutionAliasResolver.AliasTarget? Target,
    bool RequiresRegistration,
    string? Error = null);
