// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.CommandLine.Invocation;
using Winsdk.Cli.Services;

namespace Winsdk.Cli.Commands;

internal class ToolCommand : Command
{
    public ToolCommand() : base("tool", "Run a build tool command with Windows SDK paths")
    {
        Aliases.Add("run-buildtool");
        this.TreatUnmatchedTokensAsErrors = false;
    }

    public class Handler(IBuildToolsService buildToolsService, ILogger<ToolCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var args = parseResult.UnmatchedTokens.ToArray();

            if (args.Length == 0)
            {
                logger.LogError("No build tool command specified.");
                logger.LogError("Usage: winsdk tool [--quiet] <command> [args...]");
                logger.LogError("Example: winsdk tool makeappx.exe pack /o /d \"./msix\" /nv /p \"./dist/app.msix\"");
                return 1;
            }

            var toolName = args[0];
            var toolArgs = args.Skip(1).ToArray();

            try
            {
                // Ensure the build tool is available, installing BuildTools if necessary
                var toolPath = await buildToolsService.EnsureBuildToolAvailableAsync(toolName, cancellationToken: cancellationToken);

                var processStartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = toolPath.FullName,
                    Arguments = string.Join(" ", toolArgs.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = System.Diagnostics.Process.Start(processStartInfo);
                if (process == null)
                {
                    logger.LogError("Failed to start process for '{ToolName}'.", toolName);
                    return 1;
                }

                process.OutputDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                    {
                        // Todo: log into stream instead of directly to console
                        Console.Out.WriteLine(e.Data);
                    }
                };
                process.ErrorDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                    {
                        // Todo: log into stream instead of directly to console
                        Console.Error.WriteLine(e.Data);
                    }
                };

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                await process.WaitForExitAsync(cancellationToken);
                return process.ExitCode;
            }
            catch (FileNotFoundException ex)
            {
                logger.LogError("Could not find '{ToolName}' in the Windows SDK Build Tools.", toolName);
                logger.LogError("Error: {ErrorMessage}", ex.Message);
                logger.LogError("Usage: winsdk tool [--quiet] <command> [args...]");
                logger.LogError("Example: winsdk tool makeappx.exe pack /o /d \"./msix\" /nv /p \"./dist/app.msix\"");
                return 1;
            }
            catch (InvalidOperationException ex)
            {
                logger.LogError("Could not install or find Windows SDK Build Tools.");
                logger.LogError("Error: {ErrorMessage}", ex.Message);
                return 1;
            }
            catch (Exception ex)
            {
                logger.LogError("Error executing '{ToolName}': {ErrorMessage}", toolName, ex.Message);
                return 1;
            }
        }
    }
}
