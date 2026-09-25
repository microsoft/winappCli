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

    public static Option<string?> ManagedRootOption { get; } = new("--managed-root");
    public static Option<bool> LoopbackOption { get; } = new("--loopback");

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
        Options.Add(ManagedRootOption);
        Options.Add(LoopbackOption);
    }

    /// <summary>Runs the agent, or its self-test.</summary>
    public class Handler(
        IGuestSessionProbe sessionProbe,
        IGuestProcessHostFactory processes,
        IAppLauncherService appLauncher,
        IPackageRegistrationService packageRegistration)
        : AsynchronousCommandLineAction
    {
        /// <summary>Identity reader seam for verifying which dispatch paths hash the executable.</summary>
        internal Func<CancellationToken, Task<GuestAgentIdentity>> ReadIdentity { get; set; } =
            GuestAgentIdentity.ForCurrentProcessAsync;

        /// <inheritdoc/>
        public override async Task<int> InvokeAsync(
            ParseResult parseResult,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(parseResult);

            if (parseResult.GetValue(SelfTestOption))
            {
                var identity = await ReadIdentity(cancellationToken).ConfigureAwait(false);
                return RunSelfTest(parseResult, identity);
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
                parseResult.GetValue(ManagedRootOption) ?? GuestAgentRunner.DefaultManagedRoot,
                cancellationToken,
                loopbackOnly: parseResult.GetValue(LoopbackOption)).ConfigureAwait(false);
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
