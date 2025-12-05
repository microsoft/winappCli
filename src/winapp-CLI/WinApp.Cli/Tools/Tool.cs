// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace WinApp.Cli.Tools;

/// <summary>
/// Base class for Windows SDK build tools
/// </summary>
public abstract class Tool
{
    /// <summary>
    /// The executable name (e.g., "signtool.exe", "makeappx.exe")
    /// </summary>
    public abstract string ExecutableName { get; }

    /// <summary>
    /// Print error text from stdout/stderr.
    /// Tool can parse and print what it believes to be the error output.
    /// Default implementation prints both stdout and stderr.
    /// </summary>
    /// <param name="stdout">Standard output from the tool</param>
    /// <param name="stderr">Standard error from the tool</param>
    /// <param name="logger">Logger for outputting error messages</param>
    public virtual void PrintErrorText(string stdout, string stderr, ILogger logger)
    {
        // We don't know this tool, so print all output.
        if (!string.IsNullOrWhiteSpace(stdout))
        {
            logger.LogError("{Stdout}", stdout);
        }
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            logger.LogError("{Stderr}", stderr);
        }
    }
}
