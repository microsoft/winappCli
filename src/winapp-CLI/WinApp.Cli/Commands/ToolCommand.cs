// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.CommandLine.Invocation;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal class ToolCommand : Command
{
    public ToolCommand() : base("tool", "Run a build tool command with Windows SDK paths")
    {
        Aliases.Add("run-buildtool");
        this.TreatUnmatchedTokensAsErrors = false;
    }

    public class Handler(IBuildToolsService buildToolsService, IStatusService statusService, ILogger<ToolCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var args = parseResult.UnmatchedTokens.ToArray();

            if (args.Length == 0)
            {
                logger.LogError("No build tool command specified.");
                logger.LogError("Usage: winapp tool [--quiet] <command> [args...]");
                logger.LogError("Example: winapp tool makeappx.exe pack /o /d \"./msix\" /nv /p \"./dist/app.msix\"");
                return 1;
            }

            var toolName = args[0];
            var toolArgs = args.Skip(1).ToArray();

            return await statusService.ExecuteWithStatusAsync("Running build tool command...", async (taskContext) =>
            {
                try
                {
                    // Ensure the build tool is available, installing BuildTools if necessary
                    var toolPath = await buildToolsService.EnsureBuildToolAvailableAsync(toolName, taskContext, cancellationToken: cancellationToken);

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
                        return (1, $"Failed to start process for '{toolName}'.");
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
                    return (process.ExitCode, string.Empty);
                }
                catch (FileNotFoundException ex)
                {
                    return (1, $"Could not find '{toolName}' in the Windows SDK Build Tools." + Environment.NewLine +
                               $"Error: {ex.Message}" + Environment.NewLine +
                               "Usage: winapp tool [--quiet] <command> [args...]" + Environment.NewLine +
                               "Example: winapp tool makeappx.exe pack /o /d \"./msix\" /nv /p \"./dist/app.msix\"");
                }
                catch (InvalidOperationException ex)
                {
                    return (1, "Could not install or find Windows SDK Build Tools." + Environment.NewLine +
                               $"Error: {ex.Message}");
                }
                catch (Exception ex)
                {
                    return (1, $"Error executing '{toolName}': {ex.Message}");
                }
            });
        }
    }
}
