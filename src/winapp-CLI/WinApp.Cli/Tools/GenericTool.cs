// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Tools;

/// <summary>
/// Generic tool wrapper for build tools without specific error parsing logic
/// </summary>
public class GenericTool : Tool
{
    private readonly string _executableName;

    public GenericTool(string executableName)
    {
        _executableName = executableName;
    }

    public override string ExecutableName => _executableName;

    // Uses default PrintErrorText implementation (prints stdout and stderr)
}
