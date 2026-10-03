// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.WindowsSandbox;

namespace WinApp.Cli.Commands;

/// <summary>
/// Hidden verb winapp starts in the background to end its Sandbox when its window is closed.
/// </summary>
/// <remarks>See <see cref="SandboxClientWatcher"/>. Internal; not part of the public CLI.</remarks>
internal sealed class SandboxWindowWatchCommand : Command, IShortDescription
{
    public string ShortDescription => "Internal Windows Sandbox window watcher";

    internal static readonly Option<string> InstanceIdOption = new("--instance-id") { Required = true };
    internal static readonly Option<long> ClientWindowOption = new("--client-window") { Required = true };
    internal static readonly Option<int> ClientProcessIdOption = new("--client-pid") { Required = true };
    internal static readonly Option<long> ClientStartTicksOption = new("--client-start-ticks") { Required = true };

    public SandboxWindowWatchCommand()
        : base(SandboxClientWatcher.Verb, "End the Windows Sandbox winapp started when its window closes. Internal; not part of the public CLI.")
    {
        Hidden = true;
        Options.Add(InstanceIdOption);
        Options.Add(ClientWindowOption);
        Options.Add(ClientProcessIdOption);
        Options.Add(ClientStartTicksOption);
    }

    public sealed class Handler(SandboxClientWatcher watcher) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            try
            {
                await watcher.RunAsync(
                    parseResult.GetValue(InstanceIdOption)!,
                    new SandboxClientWindow(
                        (nint)parseResult.GetValue(ClientWindowOption),
                        parseResult.GetValue(ClientProcessIdOption),
                        parseResult.GetValue(ClientStartTicksOption)),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ExecutionTargetException or IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                // Nobody is watching this background process's output; leaving the Sandbox running
                // is the safe outcome of any failure.
            }

            return 0;
        }
    }
}
