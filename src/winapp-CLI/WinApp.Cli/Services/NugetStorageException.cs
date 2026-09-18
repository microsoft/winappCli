// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

/// <summary>Required NuGet storage or configuration is unavailable or explicitly invalid.</summary>
internal sealed class NugetStorageException(string message) : InvalidOperationException(message)
{
}
