// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace WinApp.Cli.Tools;

/// <summary>
/// Tool wrapper for makeappx.exe with specific error parsing
/// </summary>
public class MakeAppxTool : Tool
{
    public override string ExecutableName => "makeappx.exe";

    /// <summary>
    /// Print error text from makeappx.exe output, extracting relevant error lines
    /// </summary>
    public override void PrintErrorText(string stdout, string stderr, ILogger logger)
    {
        // Example makeappx.exe error output -- this goes to stdout:
        //  Processing "\\?\D:\temp\rigvgl1m.zgh\priconfig.xml" as a payload file.  Its path in the package will be "priconfig.xml".
        //  Processing "\\?\D:\temp\rigvgl1m.zgh\resources.pri" as a payload file.  Its path in the package will be "resources.pri".
        //  MakeAppx : error: Error info: error C00CE169: App manifest validation error: The app manifest must be valid as per schema: Line 38, Column 7, Reason: 'InvalidValue' violates enumeration constraint of 'appContainer mediumIL'.
        //  The attribute '{http://schemas.microsoft.com/appx/manifest/uap/windows10/10}TrustLevel' with value 'InvalidValue' failed to parse.

        //  MakeAppx : error: Package creation failed.
        //  MakeAppx : error: 0x80080204 - The specified package format is not valid: The package manifest is not valid.

        // Now, find the first error line in stdout and print out the rest of the output from there.
        if (!string.IsNullOrWhiteSpace(stdout))
        {
            var lines = stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            var firstErrorIndex = -1;

            // Find the first line containing "error"
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("error", StringComparison.OrdinalIgnoreCase))
                {
                    firstErrorIndex = i;
                    break;
                }
            }

            // Print from the first error line onwards
            if (firstErrorIndex >= 0)
            {
                for (int i = firstErrorIndex; i < lines.Length; i++)
                {
                    logger.LogError("{ErrorLine}", lines[i]);
                }
                logger.LogError("{ExeName} reported an error. The above text is only part of the tool output. Please re-run with --verbose to see the full output.", ExecutableName);
                return;
            }
        }

        // If no error pattern found or stderr has content, use default behavior
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            logger.LogError("{Stderr}", stderr);
            logger.LogError("{ExeName} reported an error. The above text is only part of the tool output. Please re-run with --verbose to see the full output.", ExecutableName);
        }
        else
        {
            // Fallback to base implementation
            base.PrintErrorText(stdout, stderr, logger);
        }
    }
}
