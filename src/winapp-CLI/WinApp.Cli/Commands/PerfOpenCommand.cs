// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Text.Json;
using WinApp.Cli.Services;
using WinApp.Cli.Services.Performance;

namespace WinApp.Cli.Commands;

internal sealed class PerfOpenCommand : Command, IShortDescription
{
    internal static readonly Argument<DirectoryInfo> BundleArgument = new("bundle")
    {
        Description = "Performance evidence bundle directory.",
    };

    internal static readonly Option<string> WithOption = new("--with")
    {
        Description = "Viewer to launch: wpa or default (default: auto-select WPA for ETL, otherwise the OS file association).",
        DefaultValueFactory = _ => "auto",
    };

    public string ShortDescription => "Open a retained performance artifact without modifying it";

    public PerfOpenCommand()
        : base("open", "Safely open an original artifact from a .winappperf bundle.")
    {
        Arguments.Add(BundleArgument);
        Options.Add(WithOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    internal sealed class Handler(
        IPerformanceBundleOpener opener,
        ICurrentDirectoryProvider currentDirectory,
        ILogger<PerfOpenCommand> logger) : AsynchronousCommandLineAction
    {
        public override Task<int> InvokeAsync(
            ParseResult parseResult,
            CancellationToken cancellationToken = default)
        {
            var bundle = parseResult.GetValue(BundleArgument);
            if (bundle is null)
            {
                logger.LogError("A performance bundle directory is required.");
                return Task.FromResult(1);
            }

            var bundlePath = Path.GetFullPath(
                bundle.FullName,
                currentDirectory.GetCurrentDirectory());
            var result = opener.Open(bundlePath, parseResult.GetValue(WithOption) ?? "auto");
            if (parseResult.GetValue(WinAppRootCommand.JsonOption))
            {
                parseResult.InvocationConfiguration.Output.WriteLine(JsonSerializer.Serialize(
                    result,
                    PerformanceJsonContext.Default.PerformanceOpenResult));
            }
            else if (result.Status == "opened")
            {
                parseResult.InvocationConfiguration.Output.WriteLine(
                    $"Opened {result.Artifact} with {result.Viewer}.");
            }
            else
            {
                logger.LogError("{Message}", result.Error);
                if (result.Handoff is not null)
                {
                    parseResult.InvocationConfiguration.Output.WriteLine(result.Handoff);
                }
            }

            return Task.FromResult(result.Status == "opened" ? 0 : 1);
        }
    }
}
