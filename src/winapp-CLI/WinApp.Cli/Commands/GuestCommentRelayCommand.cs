// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.Commands;

internal sealed class GuestCommentRelayCommand : Command, IShortDescription
{
    internal const string InternalVerb = "guest-comment-relay";
    public string ShortDescription => "Relay guest comment requests to their authenticated host owner";
    internal static Option<string> BindingOption { get; } = new("--binding") { Required = true };
    internal static Option<string> TargetOption { get; } = new("--target") { Required = true };
    internal static Option<string> EpochOption { get; } = new("--epoch") { Required = true };
    internal static Option<int> PidOption { get; } = new("--pid") { Required = true };
    internal static Option<long> StartOption { get; } = new("--start") { Required = true };

    public GuestCommentRelayCommand() : base(InternalVerb, "Internal authenticated comment relay.")
    {
        Hidden = true;
        Options.Add(BindingOption);
        Options.Add(TargetOption);
        Options.Add(EpochOption);
        Options.Add(PidOption);
        Options.Add(StartOption);
    }

    internal sealed class Handler : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var session = new GuestCommentSession(parseResult.GetValue(BindingOption)!, parseResult.GetValue(TargetOption)!,
                parseResult.GetValue(EpochOption)!, new GuestProcessStart(parseResult.GetValue(PidOption), parseResult.GetValue(StartOption)));
            if (!Guid.TryParseExact(session.BindingId, "N", out _) || string.IsNullOrWhiteSpace(session.TargetId) ||
                string.IsNullOrWhiteSpace(session.Epoch) || session.Process.ProcessId <= 0 || session.Process.StartTicksUtc <= 0 ||
                !Console.IsInputRedirected || !Console.IsOutputRedirected)
            {
                await parseResult.InvocationConfiguration.Error.WriteLineAsync("The guest comment relay requires a bound launch and redirected host channel.").ConfigureAwait(false);
                return 1;
            }
            try
            {
                using var input = Console.OpenStandardInput();
                using var output = Console.OpenStandardOutput();
                await new GuestCommentEndpoint(session).RunAsync(input, output,
                    message => parseResult.InvocationConfiguration.Error.WriteLine(message), cancellationToken).ConfigureAwait(false);
                return 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or
                System.ComponentModel.Win32Exception or System.Text.Json.JsonException)
            {
                await parseResult.InvocationConfiguration.Error.WriteLineAsync($"Host-backed comments are unavailable: {ex.Message}").ConfigureAwait(false);
                return 1;
            }
        }
    }
}
