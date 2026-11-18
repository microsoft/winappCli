// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Helpers;

namespace WinApp.Cli.Models;

/// <param name="Required">DevTools was asked for, so an alias that can't be prepared stops the run before anything is registered.</param>
internal sealed record InspectorAliasRequest(string? ApplicationId = null, bool Required = false);

internal sealed record InspectorAlias(
    string? AliasName,
    ExecutionAliasResolver.AliasTarget? Target,
    bool RequiresRegistration,
    string? Error = null);
