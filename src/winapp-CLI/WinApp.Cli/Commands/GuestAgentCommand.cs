// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

/// <summary>
/// The private persistent mode winapp runs in inside a guest (spec §"Guest winapp agent mode").
/// </summary>
/// <remarks>
/// Hidden rather than public: it is an internal transport endpoint, not a user-facing verb, and
/// exposing it would invite callers to depend on a contract that is versioned with the guest
/// protocol rather than with the CLI.
/// <para>
/// The agent implements no application semantics. It runs ordinary guest winapp child commands, so
/// a command behaves identically whether it was typed locally or forwarded into a Sandbox.
/// </para>
/// </remarks>
internal class GuestAgentCommand : Command, IShortDescription
{
    /// <inheritdoc/>
    public string ShortDescription => "Run winapp as a persistent execution-target guest agent";

    /// <summary>Verifies the binary can initialise, then exits without serving.</summary>
    public static Option<bool> SelfTestOption { get; } = new(GuestAgentCommandNames.SelfTestOption)
    {
        Description = "Verify this binary can run as a guest agent, then exit.",
    };

    /// <summary>Generation identity this agent serves; requests from any other are refused.</summary>
    public static Option<string?> TargetEpochOption { get; } = new("--target-epoch")
    {
        Description = "Target generation this agent serves.",
    };

    /// <summary>Directory the agent publishes its heartbeat and startup diagnostics to.</summary>
    public static Option<string?> ResultDirectoryOption { get; } = new("--result-dir")
    {
        Description = "Bounded writable folder for startup diagnostics and readiness output.",
    };

    /// <summary>Read-only directory holding this boot's connection material.</summary>
    public static Option<string?> BootstrapDirectoryOption { get; } = new("--bootstrap-dir")
    {
        Description = "Read-only folder holding this boot's connection material.",
    };

    /// <summary>TCP port to listen on; 0 selects an ephemeral port.</summary>
    public static Option<int> PortOption { get; } = new("--port")
    {
        Description = "TCP port to listen on. 0 selects an ephemeral port.",
        DefaultValueFactory = _ => 0,
    };

    /// <summary>Runs winapp as the per-operation job containment barrier.</summary>
    public static Option<bool> OperationHostOption { get; } = new(GuestOperationHost.OperationHostOption)
    {
        Description = "Wait to be placed in a job object, then run the command after '--'.",
    };

    /// <summary>Event the agent signals once the barrier is a job member.</summary>
    public static Option<string?> ReadyEventOption { get; } = new(GuestOperationHost.ReadyEventOption)
    {
        Description = "Name of the event signalled once this process has been assigned to its job.",
    };

    /// <summary>The command the barrier runs once released.</summary>
    public static Argument<string[]> BarrierCommandArgument { get; } = new("command")
    {
        Description = "Command to run once this process is inside its job.",
        Arity = ArgumentArity.ZeroOrMore,
    };

    /// <summary>Creates the hidden agent verb.</summary>
    public GuestAgentCommand()
        : base(
            GuestAgentCommandNames.Verb,
            "Run winapp as a persistent guest agent for an execution target. Internal; not part of the public CLI.")
    {
        // Hidden keeps it out of help and completions. It stays in the CLI schema's command tree
        // because the schema documents what exists, and a tool that shells into a guest needs to
        // know this verb is reserved.
        Hidden = true;

        Options.Add(SelfTestOption);
        Options.Add(TargetEpochOption);
        Options.Add(BootstrapDirectoryOption);
        Options.Add(ResultDirectoryOption);
        Options.Add(PortOption);
        Options.Add(OperationHostOption);
        Options.Add(ReadyEventOption);
        Arguments.Add(BarrierCommandArgument);
    }

    /// <summary>Runs the agent, or its self-test.</summary>
    public class Handler(
        IGuestSessionProbe sessionProbe,
        IGuestProcessHostFactory processes,
        IAppLauncherService appLauncher,
        IPackageRegistrationService packageRegistration)
        : AsynchronousCommandLineAction
    {
        /// <inheritdoc/>
        public override async Task<int> InvokeAsync(
            ParseResult parseResult,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(parseResult);

            var identity = await GuestAgentIdentity
                .ForCurrentProcessAsync(cancellationToken)
                .ConfigureAwait(false);

            if (parseResult.GetValue(SelfTestOption))
            {
                return RunSelfTest(parseResult, identity);
            }

            if (parseResult.GetValue(OperationHostOption))
            {
                // The containment barrier. It must not start anything until the agent has placed it
                // in the operation's job, which is what makes per-operation cancellation able to
                // take the whole process tree with it.
                return await GuestOperationHost.RunAsync(
                    parseResult.GetValue(ReadyEventOption) ?? string.Empty,
                    parseResult.GetValue(BarrierCommandArgument) ?? [],
                    workingDirectory: null,
                    cancellationToken).ConfigureAwait(false);
            }

            var bootstrapDirectory = parseResult.GetValue(BootstrapDirectoryOption);
            var resultDirectory = parseResult.GetValue(ResultDirectoryOption);

            // Serving is reached only through a target backend, which supplies the connection
            // material. Invoked directly there is nothing to connect to, and silently idling would
            // look like a hang.
            if (string.IsNullOrWhiteSpace(bootstrapDirectory) || string.IsNullOrWhiteSpace(resultDirectory))
            {
                parseResult.InvocationConfiguration.Error.WriteLine(
                    "winapp guest-agent is started by winapp inside an execution target and cannot be run directly.");
                return 1;
            }

            return await new GuestAgentRunner(sessionProbe, processes, appLauncher, packageRegistration).RunAsync(
                bootstrapDirectory,
                resultDirectory,
                GuestAgentRunner.DefaultManagedRoot,
                cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Proves this binary can initialise as an agent.
        /// </summary>
        /// <remarks>
        /// The self-test deliberately does not require an interactive desktop. A candidate is
        /// staged and tested before <c>wsb connect</c> has necessarily re-established the session,
        /// and failing it for a transient desktop state would reject a perfectly good binary. What
        /// it does prove is that the image loads, the runtime initialises, protocol constants are
        /// consistent, and the session probe runs without faulting — which is exactly the class of
        /// failure that must never reach activation.
        /// </remarks>
        private int RunSelfTest(ParseResult parseResult, GuestAgentIdentity identity)
        {
            var output = parseResult.InvocationConfiguration.Output;

            if (identity.ProtocolMinimum > identity.ProtocolMaximum)
            {
                parseResult.InvocationConfiguration.Error.WriteLine(
                    "The guest agent's protocol range is inconsistent.");
                return 1;
            }

            GuestSessionInfo session;
            try
            {
                session = sessionProbe.Probe();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                parseResult.InvocationConfiguration.Error.WriteLine(
                    $"The guest agent could not read its Windows session: {ex.Message}");
                return 1;
            }

            var heartbeat = GuestAgentHeartbeat.Create(
                identity,
                GuestAgentReadiness.Evaluate(session),
                ExecutionTargetEpoch.None,
                port: 0,
                DateTimeOffset.UtcNow);

            // Emitting the heartbeat proves serialization works too, and gives a human running the
            // self-test by hand something to read.
            output.WriteLine(heartbeat.ToJson());
            return 0;
        }
    }
}
