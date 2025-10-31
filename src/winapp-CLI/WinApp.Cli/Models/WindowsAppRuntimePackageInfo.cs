// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Models;

/// <summary>
/// Information about a Windows App Runtime package from the MSIX inventory
/// </summary>
public class WindowsAppRuntimePackageInfo
{
    public required string RuntimeName { get; set; }
    public required string MinVersion { get; set; }
}
