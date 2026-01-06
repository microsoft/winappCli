// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.CommandLine.Invocation;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal class MSStoreCommand : Command
{
    public MSStoreCommand() : base("store", "Run a Microsoft Store Developer CLI command.")
    {
        this.TreatUnmatchedTokensAsErrors = false;
    }

    public class Handler(IMSStoreCLIService msStoreCLIService, ILogger<MSStoreCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var args = parseResult.UnmatchedTokens.ToArray();

            var msstoreArgs = args.ToArray();

            try
            {
                // Ensure the build tool is available, installing BuildTools if necessary
                await msStoreCLIService.EnsureMSStoreCLIAvailableAsync(cancellationToken: cancellationToken);

                var processStartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "msstore",
                    Arguments = string.Join(" ", msstoreArgs.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)),
                    RedirectStandardInput = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                    UseShellExecute = false,
                    CreateNoWindow = false
                };

                using var process = System.Diagnostics.Process.Start(processStartInfo);
                if (process == null)
                {
                    logger.LogError("Failed to start process for MSStoreCLI.");
                    return 1;
                }

                await process.WaitForExitAsync(cancellationToken);
                return process.ExitCode;
            }
            catch (Exception ex)
            {
                logger.LogError("Error executing MSStoreCLI: {ErrorMessage}", ex.Message);
                return 1;
            }
        }
    }
}
