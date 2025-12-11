// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace WinApp.Cli.Tools;

/// <summary>
/// Tool wrapper for makepri.exe with specific error parsing
/// </summary>
public partial class MakePriTool : Tool
{
    public override string ExecutableName => "makepri.exe";

    /// <summary>
    /// Print error text from makepri.exe output, extracting relevant error lines
    /// </summary>
    public override void PrintErrorText(string stdout, string stderr, ILogger logger)
    {
        // makepri.exe writes errors to stderr, so just print stderr if there is any.
        if (string.IsNullOrWhiteSpace(stderr))
        {
            base.PrintErrorText(stdout, stderr, logger);
        }
        else
        {
            logger.LogError("{Stderr}", stderr);
            logger.LogError("{ExeName} reported an error. The above text is only part of the tool output. Please re-run with --verbose to see the full output.", ExecutableName);
        }
    }
}
