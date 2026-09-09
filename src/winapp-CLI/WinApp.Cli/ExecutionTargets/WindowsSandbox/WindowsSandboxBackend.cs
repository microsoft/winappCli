// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.ExecutionTargets.WindowsSandbox;

/// <summary>
/// The Windows Sandbox execution-target backend (spec §"Target backend").
/// </summary>
/// <remarks>
/// Everything Windows Sandbox specific lives here and in <see cref="WindowsSandboxCli"/>: instance
/// lifecycle, the read-only bootstrap share, guest IP discovery, and the interactive client. Above
/// this boundary orchestration sees only an <see cref="IGuestTransport"/> and an epoch, which is
/// what lets a future Hyper-V or remote backend reuse deployment, runtime, UI, and artifact handling
/// unchanged.
/// <para>
/// <c>wsb exec</c> is used for exactly one thing: launching the agent. It takes the command as a
/// single string and relays only an exit code, so it can neither carry argument boundaries nor
/// report what went wrong. The agent therefore writes its own diagnostics into the writable
/// bootstrap-result folder, and a failure to start surfaces those rather than a bare transport
/// error.
/// </para>
/// </remarks>
internal sealed class WindowsSandboxBackend(
    IWindowsSandboxCli cli,
    WindowsSandboxLifecycle lifecycle,
    ITargetStateDirectoryProvider directoryProvider,
    IHostWinappBinaryProvider hostBinaryProvider,
    IWindowsSandboxWindowController windowController,
    IWindowsSandboxSetup? setup = null,
    ITargetStateStore? stateStore = null) : IExecutionTargetBackend, IHostRenderedTarget, IInspectableTarget
{
    /// <summary>Guest path prefix the read-only bootstrap folder is mapped under.</summary>
    /// <remarks>
    /// A per-epoch suffix is appended. Reusing one fixed path would mean an adopted guest — which
    /// may already have a mapped folder of that name from a previous manager, or an agent still
    /// running out of it — could not be told apart from a folder this host just mapped. Each
    /// generation gets its own name so nothing stale can be mistaken for the current one.
    /// </remarks>
    internal const string GuestBootstrapPathPrefix = @"C:\WinAppBootstrap-";

    /// <summary>Guest path prefix the writable bootstrap-result folder is mapped under.</summary>
    internal const string GuestResultPathPrefix = @"C:\WinAppBootstrapResult-";

    /// <summary>Host folder name prefix for the read-only bootstrap share.</summary>
    private const string BootstrapFolderPrefix = "bootstrap-";

    /// <summary>Host folder name prefix for the writable bootstrap-result share.</summary>
    private const string ResultFolderPrefix = "bootstrap-result-";

    /// <summary>How many previous generations' bootstrap folders are kept before pruning.</summary>
    /// <remarks>
    /// Old folders stay mapped for the life of the Sandbox that mapped them, so they cannot always
    /// be deleted. Keeping a small number and best-effort deleting the rest bounds the growth
    /// without ever failing a command over a folder Windows still holds open.
    /// </remarks>
    internal const int MaxRetainedBootstrapGenerations = 4;

    /// <summary>Native image encoder shipped beside the AOT CLI.</summary>
    private const string SkiaCompanionName = "libSkiaSharp.dll";

    /// <summary>How long the host waits for the agent to publish a readiness heartbeat.</summary>
    internal static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromMinutes(3);

    /// <summary>Delay while the connected client is still establishing the interactive login.</summary>
    internal static readonly TimeSpan AgentLaunchRetryDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How long to wait for a client winapp reconnected to make the desktop input-ready.
    /// </summary>
    /// <remarks>
    /// Much shorter than the heartbeat timeout on purpose. A client that has just been attached to
    /// an already-running guest becomes input-ready in seconds, so continuing to the full deadline
    /// would turn a recoverable condition into minutes of silence before reporting the same thing.
    /// It bounds the retry loop, not a single wait: a guest that stops publishing anything at all is
    /// still held by <see cref="HeartbeatTimeout"/> inside one <see cref="WaitForHeartbeatAsync"/>
    /// call.
    /// </remarks>
    internal static readonly TimeSpan ClientReconnectReadyTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Clock seam, so agent-readiness bounds are exercised without real time passing.</summary>
    internal Func<DateTimeOffset> UtcNow { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>Delay seam, so agent-readiness bounds are exercised without real waiting.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    /// <summary>
    /// How long to spend deciding whether an agent that closed a handshake is still listening.
    /// </summary>
    /// <remarks>
    /// Short on purpose. It only runs on a failure path, and it answers a question a live local
    /// agent settles in milliseconds; waiting longer would just delay the repair a dead one needs.
    /// </remarks>
    internal static readonly TimeSpan AgentLivenessProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Most files the untrusted result folder may contain before it is refused.</summary>
    internal const int MaxResultFiles = 16;

    /// <summary>Largest total size the untrusted result folder may reach before it is refused.</summary>
    internal const long MaxResultBytes = 1024 * 1024;

    /// <summary>
    /// Lowest port the guest agent is asked to listen on.
    /// </summary>
    /// <remarks>
    /// The IANA dynamic/private range. The host picks the port rather than letting the agent bind
    /// port 0, because the inbound firewall rule has to exist <em>before</em> the listener starts —
    /// see <see cref="AllowGuestAgentConnectionAsync"/> — and a rule cannot name a port nobody has
    /// chosen yet.
    /// </remarks>
    internal const int MinAgentPort = 49152;

    /// <summary>Highest port the guest agent is asked to listen on.</summary>
    internal const int MaxAgentPort = 65535;

    private string? _instanceId;
    private string? _guestAddress;
    private bool _adopted;
    private SandboxClientWindow? _client;
    private GuestBootstrapMaterial? _activeMaterial;

    /// <summary>The host and guest paths one generation's bootstrap share is mapped through.</summary>
    /// <param name="HostBootstrap">Host folder published read-only into the guest.</param>
    /// <param name="HostResult">Host folder the guest may write its handshake results into.</param>
    /// <param name="GuestBootstrap">Guest path the read-only folder appears at.</param>
    /// <param name="GuestResult">Guest path the writable folder appears at.</param>
    internal sealed record BootstrapShare(
        string HostBootstrap,
        string HostResult,
        string GuestBootstrap,
        string GuestResult);

    /// <summary>Picks a listening port for the guest agent from the dynamic range.</summary>
    /// <remarks>
    /// Random rather than fixed so two unrelated guests do not collide on a well-known number, and
    /// so a stale rule from a previous boot does not silently authorise a new agent. A collision
    /// inside the guest is handled by retrying the bootstrap with a fresh port rather than by
    /// falling back to port 0, which would reintroduce the ordering problem.
    /// </remarks>
    internal static int NextAgentPort() =>
        RandomNumberGenerator.GetInt32(MinAgentPort, MaxAgentPort + 1);

    /// <inheritdoc/>
    public ExecutionTargetRef Target => WindowsSandboxTarget.Default;

    /// <inheritdoc/>
    /// <remarks>
    /// <b>Not read-only, despite the name.</b> The name comes from
    /// <see cref="IExecutionTargetBackend"/>; for Windows Sandbox this is the point at which
    /// <c>--on sandbox</c>'s consent is spent, so it can enable a Windows feature behind a UAC prompt
    /// and install the Sandbox client. Anything that only wants to know the host's state must call
    /// <see cref="IWindowsSandboxSetup.InspectAsync"/> instead.
    /// </remarks>
    public async Task<TargetSupportResult> ProbeSupportAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return TargetSupportResult.Unsupported(new ExecutionTargetErrorInfo
            {
                Code = ExecutionTargetErrorCodes.Unsupported,
                Message = "Windows Sandbox execution requires Windows.",
                UserAction = "Run this command on a Windows 11 machine with Windows Sandbox installed.",
            });
        }

        // Probed before the application is built, so anything unusable fails in seconds rather than
        // after a long build. There is never a silent fallback to local execution.
        //
        // Deliberately NOT short-circuited on `cli.IsAvailable`. That only proves a `wsb.exe` file
        // resolves, and the alias is a zero-byte APPEXECLINK whose package may never have
        // initialized -- which is precisely the host state that used to fail. The setup runner is
        // the thing that asks `wsb --version`, and it returns immediately when the answer is yes, so
        // routing through it costs one cheap probe and closes the hole.
        if (setup is null)
        {
            return cli.IsAvailable
                ? TargetSupportResult.Supported
                : TargetSupportResult.Unsupported(new ExecutionTargetErrorInfo
                {
                    Code = ExecutionTargetErrorCodes.Unsupported,
                    Message = "The Windows Sandbox command line (wsb.exe) was not found.",
                    UserAction = "Install Windows Sandbox on Windows 11 24H2 or newer, then retry.",
                    Example = "winapp run . --on sandbox",
                });
        }

        // `--on sandbox` is explicit consent to make Windows Sandbox usable, so missing prerequisites
        // are installed rather than reported. Only what winapp genuinely cannot do -- elevation the
        // user declined, a restart, an unsupported edition, a Store or policy failure -- comes back
        // as an error, and it says exactly which of those it was.
        try
        {
            var facts = await setup.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

            if (facts.ExecutablePath is { } executable)
            {
                cli.UseExecutable(executable);
            }

            return TargetSupportResult.Supported;
        }
        catch (ExecutionTargetException ex)
        {
            return TargetSupportResult.Unsupported(ex.Error);
        }
    }

    /// <inheritdoc/>
    public async Task<TargetConnection> EnsureConnectedAsync(
        EnsureTargetOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var lease = await lifecycle.EnsureInstanceAsync(cancellationToken).ConfigureAwait(false);
        _instanceId = lease.InstanceId;
        _adopted = lease.IsAdopted;

        // Reconnecting to an agent that is already serving is the whole point of a persistent
        // Sandbox: it costs one TCP connect instead of a client reconnect, an agent relaunch, and a
        // runtime re-verification. It is attempted before anything else touches the instance.
        //
        // Only a genuinely warm lease qualifies. A recovered or adopted instance is running, but
        // nothing in it was prepared under the epoch that now identifies it, so its "persisted"
        // material describes a generation that no longer exists.
        if (lease.IsWarm &&
            await TryReconnectAsync(lease, remember: true, cancellationToken).ConfigureAwait(false) is { } reused)
        {
            return reused;
        }

        var bootstrap = PrepareBootstrapDirectories(lease.Epoch);
        var agentHash = await StageBootstrapBinaryAsync(bootstrap.HostBootstrap, cancellationToken)
            .ConfigureAwait(false);

        // The port is chosen here, by the host, and written into the material the agent reads. That
        // ordering is what removes the Windows Security consent dialog: the inbound rule below can
        // only be created for a port that is already known, and Windows prompts the moment a
        // listener starts without a matching rule.
        var agentPort = NextAgentPort();
        var material = GuestBootstrapMaterial.Create(Target, lease.Epoch, agentPort);
        await File.WriteAllTextAsync(
            Path.Join(bootstrap.HostBootstrap, GuestBootstrapMaterial.FileName),
            material.ToJson(),
            cancellationToken).ConfigureAwait(false);

        // Read-only: the guest must not be able to rewrite the connection material and redirect its
        // own agent. The result folder is the only writable path, and it is bounded and treated as
        // untrusted input.
        await cli.ShareFolderAsync(
            lease.InstanceId, bootstrap.HostBootstrap, bootstrap.GuestBootstrap, allowWrite: false, cancellationToken)
            .ConfigureAwait(false);

        await cli.ShareFolderAsync(
            lease.InstanceId, bootstrap.HostResult, bootstrap.GuestResult, allowWrite: true, cancellationToken)
            .ConfigureAwait(false);

        // Real input and Windows Graphics Capture need a connected client, and connecting is also
        // what establishes the interactive login session the agent must run in.
        //
        // Whether to connect is decided from what winapp knows plus what the guest reports.
        //
        // An instance winapp started itself was started headless -- `wsb start` attaches no client --
        // so for Created and RecoveredStart the absence of a client is not a guess. Only a positive
        // "a session already exists" is allowed to skip the connect there; anything else, including
        // a probe that could draw no conclusion, connects. Skipping on Unknown would leave the agent
        // with no session to launch into and burn the whole heartbeat window discovering it.
        //
        // Adoption is the opposite case and stays conservative. Measured on a live Sandbox:
        // `wsb connect` against an instance whose client is already attached starts a *second*
        // WindowsSandboxRemoteSession, and that extra client outlives `wsb stop`. winapp did not
        // start that guest and cannot assume it is unattended, so only a confirmed missing session
        // creates a client -- and a genuinely closed client is recovered later, from the agent's own
        // evidence rather than from a guess.
        var session = await cli
            .ProbeInteractiveSessionAsync(lease.InstanceId, cancellationToken)
            .ConfigureAwait(false);

        var startedHeadlessByWinapp =
            lease.Origin is SandboxInstanceOrigin.Created or SandboxInstanceOrigin.RecoveredStart;

        var connectedClient = startedHeadlessByWinapp
            ? session is not GuestSessionAvailability.Ready
            : session is GuestSessionAvailability.NoLoginSession;

        if (connectedClient)
        {
            await ConnectClientAsync(lease.InstanceId, cancellationToken)
                .ConfigureAwait(false);
        }

        // Once per generation, before the first application is ever deployed. Registering a loose
        // layout needs Developer Mode, and it is a machine-wide setting a guest process running as
        // the interactive user cannot set — so it is done here, as one fixed privileged operation
        // with a constant command, rather than by exposing SYSTEM execution.
        //
        // An adopted guest gets it too: winapp did not start that Sandbox, so nothing has set it
        // there either.
        if (!lease.IsWarm)
        {
            await EnableGuestDevelopmentModeAsync(lease.InstanceId, cancellationToken).ConfigureAwait(false);
        }

        // Before the agent starts listening, not after. This is the fix for the firewall consent
        // prompt: the rule has to be in place when the socket opens, or Windows asks the user.
        await AllowGuestAgentConnectionAsync(
            lease.InstanceId,
            bootstrap.GuestBootstrap,
            agentPort,
            cancellationToken).ConfigureAwait(false);

        var heartbeat = await LaunchReadyAgentAsync(
            lease.InstanceId,
            bootstrap,
            lease.Epoch,
            connectedClient,
            cancellationToken).ConfigureAwait(false);

        if (!string.Equals(heartbeat.BinaryHash, agentHash, StringComparison.OrdinalIgnoreCase))
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.AgentIncompatible,
                "The Windows Sandbox agent is not the winapp binary this host staged.",
                userAction: "Close Windows Sandbox, then retry so winapp starts a fresh guest agent.",
                context: new Dictionary<string, string>
                {
                    ["expectedHash"] = agentHash,
                    ["actualHash"] = heartbeat.BinaryHash,
                });
        }

        if (heartbeat.Port != agentPort)
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.TransportFailed,
                "The Windows Sandbox agent is listening on a port the host did not authorise.",
                userAction: "Close Windows Sandbox, then retry.",
                context: new Dictionary<string, string>
                {
                    ["expectedPort"] = agentPort.ToString(CultureInfo.InvariantCulture),
                    ["actualPort"] = heartbeat.Port.ToString(CultureInfo.InvariantCulture),
                });
        }

        _guestAddress = await cli.GetIpAddressAsync(lease.InstanceId, cancellationToken).ConfigureAwait(false);

        var transport = await GuestTcpTransport.ConnectAsync(
            _guestAddress,
            material,
            cancellationToken).ConfigureAwait(false);
        _activeMaterial = material;

        RememberConnection(lease, _guestAddress);

        // The result folder has served its purpose and is guest-writable, so it does not survive the
        // handshake it was created for.
        TryClearResultFolder(bootstrap.HostResult);

        return new TargetConnection(lease.Epoch, transport, lease.IsWarm);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Built entirely out of the two operations that already change nothing: the reconciliation that
    /// compares persisted state against <c>wsb list</c>, and the reconnect that attaches to an agent
    /// already serving this epoch. Neither creates an instance, connects a client, or stages an
    /// agent, so this answers "what is there?" without becoming the reason something is.
    /// <para>
    /// No connection lock is taken. Waiting for another winapp process to finish bootstrapping would
    /// make a report of the current state block for minutes on the state changing; a snapshot taken
    /// mid-bootstrap honestly says the agent is not answering yet.
    /// </para>
    /// </remarks>
    public async Task<TargetAttachment> TryAttachAsync(CancellationToken cancellationToken)
    {
        var reconciliation = await lifecycle.ReconcileAsync(cancellationToken).ConfigureAwait(false);

        if (reconciliation.State != TargetLifecycleState.Running ||
            reconciliation.InstanceId is not { } instanceId)
        {
            return TargetAttachment.NotRunning;
        }

        _instanceId = instanceId;

        // Warm means a previous command finished bootstrapping this exact epoch, which is the only
        // case where connection material on disk describes an agent that is actually there. Anything
        // else has no agent to attach to, and manufacturing one is what this method exists not to do.
        var state = stateStore?.Read(Target);
        var isWarm = string.Equals(
            state?.BootstrappedEpoch, reconciliation.Epoch.Value, StringComparison.Ordinal);

        if (!isWarm)
        {
            return new TargetAttachment(true, reconciliation.Epoch, null);
        }

        var lease = new SandboxInstanceLease(
            instanceId, reconciliation.Epoch, SandboxInstanceOrigin.Reused, IsWarm: true);

        var connection = await TryReconnectAsync(lease, remember: false, cancellationToken)
            .ConfigureAwait(false);

        return new TargetAttachment(true, reconciliation.Epoch, connection);
    }

    /// <summary>
    /// Reconnects to an agent that is already serving this epoch, or returns null.
    /// </summary>
    /// <param name="lease">The instance and epoch to reconnect to.</param>
    /// <param name="remember">
    /// Whether a successful reconnect may be written back to persisted state. False for inspection,
    /// which must be able to answer "what is there?" without becoming a revision in the answer: a
    /// snapshot that recorded the address it just looked up would change the file every caller is
    /// asking it to describe, and would contend for it with the command actually doing the work.
    /// </param>
    /// <param name="cancellationToken">Cancels the reconnect.</param>
    /// <remarks>
    /// <para>
    /// Every CLI invocation constructs a fresh backend, so in-process fields alone can never make
    /// the second command reuse the first one's agent — the material has to come off disk. It is
    /// read back from the same read-only bootstrap file the agent itself was given, so host and
    /// guest cannot disagree about the key, and the epoch inside it is checked against the lease so
    /// material from a previous boot is never used against a new one.
    /// </para>
    /// <para>
    /// Returning null means "reconnect was not possible", never "the Sandbox is unusable": the
    /// caller falls through to a full bootstrap, which repairs the agent without recreating the
    /// instance. A failure here must not be reported as an error, because a stopped agent is an
    /// ordinary state after the guest reboots or the agent is killed.
    /// </para>
    /// </remarks>
    private async Task<TargetConnection?> TryReconnectAsync(
        SandboxInstanceLease lease,
        bool remember,
        CancellationToken cancellationToken)
    {
        var material = _activeMaterial ?? TryReadPersistedMaterial(lease.Epoch);

        // Port 0 is what an older build wrote before the host assigned ports, and it is also what
        // "bind anywhere" means — never a port anything is listening on. Treated as no material at
        // all so a stale file makes this repair rather than attempt a meaningless connect.
        if (material is null ||
            !string.Equals(material.TargetEpoch, lease.Epoch.Value, StringComparison.Ordinal) ||
            material.Port is <= 0 or > IPEndPoint.MaxPort)
        {
            return null;
        }

        var address = _guestAddress ?? TryReadPersistedAddress(lease);

        if (string.IsNullOrWhiteSpace(address))
        {
            try
            {
                address = await cli.GetIpAddressAsync(lease.InstanceId, cancellationToken).ConfigureAwait(false);
            }
            catch (ExecutionTargetException)
            {
                return null;
            }
        }

        try
        {
            var transport = await GuestTcpTransport
                .ConnectAsync(address, material, cancellationToken)
                .ConfigureAwait(false);

            _guestAddress = address;
            _activeMaterial = material;

            if (remember)
            {
                RememberConnection(lease, address);
            }

            return new TargetConnection(lease.Epoch, transport, Reused: true);
        }
        catch (ExecutionTargetException ex)
        {
            _activeMaterial = null;

            // A peer that accepted the connection and then closed it was alive a moment ago, which
            // an agent at its channel ceiling is and a dead agent is not. The two are identical on
            // the wire, so they are told apart by asking whether anything is still listening rather
            // than by guessing. Repairing a live agent would stage and relaunch one underneath the
            // channels the running agent is still serving.
            if (ex.Error.Context?.ContainsKey(GuestSecureChannel.ClosedDuringHandshakeKey) == true &&
                await GuestTcpTransport.IsListeningAsync(
                    address, material.Port, AgentLivenessProbeTimeout, cancellationToken).ConfigureAwait(false))
            {
                throw ExecutionTargetException.Create(
                    ExecutionTargetErrorCodes.AgentBusy,
                    "The Windows Sandbox agent is serving as many winapp commands as it allows.",
                    userAction: "Wait for one of the running commands to finish, then retry.",
                    innerException: ex);
            }

            // The Sandbox is alive but its agent is not answering. Repair only that layer; the
            // unchanged epoch keeps deployment and runtime state valid across the repair.
            return null;
        }
    }

    /// <summary>Reads the connection material this target last bootstrapped for one epoch.</summary>
    /// <remarks>
    /// Scoped to the epoch's own folder, so material written for a previous generation is not even
    /// a candidate — the file cannot be there to be mistakenly matched.
    /// </remarks>
    private GuestBootstrapMaterial? TryReadPersistedMaterial(ExecutionTargetEpoch epoch)
    {
        try
        {
            var path = Path.Join(
                directoryProvider.GetTargetRoot(Target, create: false).FullName,
                BootstrapFolderPrefix + EpochToken(epoch),
                GuestBootstrapMaterial.FileName);

            return File.Exists(path) ? GuestBootstrapMaterial.TryParse(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or ExecutionTargetException)
        {
            return null;
        }
    }

    /// <summary>The guest address recorded alongside this instance, when it is still the same one.</summary>
    private string? TryReadPersistedAddress(SandboxInstanceLease lease)
    {
        var state = stateStore?.Read(Target);

        return string.Equals(state?.InstanceId, lease.InstanceId, StringComparison.OrdinalIgnoreCase)
            ? state?.GuestAddress
            : null;
    }

    /// <summary>
    /// Records the guest address so the next process can reconnect without re-querying it.
    /// </summary>
    /// <remarks>
    /// Best effort and never fatal. The address is a cache, not a source of truth — losing it costs
    /// one <c>wsb</c> call on the next command, while failing the command over it would trade a
    /// working Sandbox for an error.
    /// </remarks>
    private void RememberConnection(SandboxInstanceLease lease, string address)
    {
        if (stateStore is null)
        {
            return;
        }

        try
        {
            var state = stateStore.Read(Target);

            if (state is null ||
                !string.Equals(state.InstanceId, lease.InstanceId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var addressChanged = !string.IsNullOrWhiteSpace(address) &&
                !string.Equals(state.GuestAddress, address, StringComparison.OrdinalIgnoreCase);

            // The bootstrap marker is written here, and only here, because reaching this point is
            // what proves the guest is actually talkable-to: the client is connected, the agent
            // published a matching heartbeat, and the authenticated channel is open. Recording it
            // when ownership was claimed instead would let a command killed mid-bootstrap leave an
            // instance the next command treats as warm.
            var bootstrapChanged = !string.Equals(
                state.BootstrappedEpoch, lease.Epoch.Value, StringComparison.Ordinal);

            var clientChanged = _client is not null && ReadClient(state) != _client;

            if (!addressChanged && !bootstrapChanged && !clientChanged)
            {
                return;
            }

            stateStore.Commit(
                Target,
                state with
                {
                    GuestAddress = addressChanged ? address : state.GuestAddress,
                    BootstrappedEpoch = lease.Epoch.Value,
                    ClientWindowHandle = clientChanged ? _client!.Handle : state.ClientWindowHandle,
                    ClientProcessId = clientChanged ? _client!.ProcessId : state.ClientProcessId,
                    ClientProcessStartTicksUtc =
                        clientChanged ? _client!.StartTicksUtc : state.ClientProcessStartTicksUtc,
                    ClientOwnedByWinapp = clientChanged || state.ClientOwnedByWinapp,
                },
                state.Revision);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExecutionTargetException)
        {
            // A stale or contended state file costs a re-bootstrap next time, nothing more.
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The recorded client is only a hint. It is offered to the controller, which uses it only while
    /// that exact window is still open, so a handle persisted by an earlier winapp process can never
    /// resolve against whatever owns that number now. When there is no usable record — an adopted
    /// Sandbox, or state written before this was recorded — the controller may read the single open
    /// client as adopted, but never persists or caches it as owned.
    /// </remarks>
    public TargetDesktopSurface ResolveDesktopSurface(TargetDesktopUse use)
    {
        var remembered = _client ?? TryReadPersistedClient();
        var status = windowController.EnsureClientReady(remembered, use);
        var client = status.Window;

        if (remembered is not null && client == remembered && client != _client)
        {
            _client = client;
        }

        return Describe(status, remembered);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The same resolution, minus every trace of having asked. Nothing is written back and nothing
    /// is cached, so repeating a snapshot leaves the state file at the revision it was already at —
    /// which is what lets an agent poll a target without racing the command that is preparing it.
    /// <para>
    /// An adopted client is reported as adopted here exactly as it is elsewhere, and is read where
    /// it stands: inspection never moves a window it did not open.
    /// </para>
    /// </remarks>
    public TargetDesktopSurface InspectDesktopSurface()
    {
        var remembered = _client ?? TryReadPersistedClient();

        return Describe(windowController.InspectClient(remembered), remembered);
    }

    /// <summary>Describes a resolved client for a caller.</summary>
    private static TargetDesktopSurface Describe(
        SandboxClientStatus status,
        SandboxClientWindow? remembered) =>
        new(
            status.Window.Handle,
            status.Window.ProcessId,
            WindowsSandboxWindowController.RemoteSessionProcessName,
            Adopted: status.Window != remembered,
            IsMinimized: status.IsMinimized);

    /// <summary>The client window recorded for this instance, when it is still the same one.</summary>
    private SandboxClientWindow? TryReadPersistedClient()
    {
        try
        {
            var state = stateStore?.Read(Target);

            return _instanceId is not null &&
                string.Equals(state?.InstanceId, _instanceId, StringComparison.OrdinalIgnoreCase)
                ? ReadClient(state!)
                : null;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or ExecutionTargetException)
        {
            return null;
        }
    }

    /// <summary>The persisted client triple, or null when any part of it is missing.</summary>
    private static SandboxClientWindow? ReadClient(TargetState state) =>
        state is
        {
            ClientOwnedByWinapp: true,
            ClientWindowHandle: { } handle,
            ClientProcessId: { } pid,
            ClientProcessStartTicksUtc: { } ticks,
        }
            ? new SandboxClientWindow((nint)handle, pid, ticks)
            : null;

    /// <summary>
    /// Records a client window proven to have been created by winapp, best effort.
    /// </summary>
    /// <remarks>
    /// Never fatal: losing it only means the next command treats the single open window as adopted,
    /// and failing a connection over a contended state file would be worse. Adopted/manual clients
    /// are deliberately never written here: remembering one would later make it look winapp-owned
    /// and allow minimized restore to move the user's window off-screen.
    /// </remarks>
    private void RememberClientWindow(SandboxClientWindow client)
    {
        if (stateStore is null || _instanceId is null)
        {
            return;
        }

        try
        {
            var state = stateStore.Read(Target);

            if (state is null ||
                !string.Equals(state.InstanceId, _instanceId, StringComparison.OrdinalIgnoreCase) ||
                ReadClient(state) == client)
            {
                return;
            }

            stateStore.Commit(
                Target,
                state with
                {
                    ClientWindowHandle = client.Handle,
                    ClientProcessId = client.ProcessId,
                    ClientProcessStartTicksUtc = client.StartTicksUtc,
                    ClientOwnedByWinapp = true,
                },
                state.Revision);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or ExecutionTargetException)
        {
        }
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, string> DescribeForDiagnostics()
    {
        var description = new Dictionary<string, string>(StringComparer.Ordinal);

        if (_instanceId is { } id)
        {
            description["sandboxId"] = id;
        }

        if (_guestAddress is { } address)
        {
            description["guestAddress"] = address;
        }

        if (_adopted)
        {
            // Reported so a failure envelope says plainly that the Sandbox in play is one winapp
            // took over rather than one it started -- and therefore one it will not stop.
            description["sandboxAdopted"] = "true";
        }

        return description;
    }

    /// <summary>
    /// A short, path-safe, per-generation name derived from the epoch.
    /// </summary>
    /// <remarks>
    /// Hashed rather than used directly because the epoch contains a colon and an instance ID, and
    /// this value becomes both a host folder name and a guest path. Sixteen hexadecimal characters
    /// of SHA-256 is far more than enough to keep generations distinct while keeping the guest path
    /// short enough to read in a log.
    /// </remarks>
    internal static string EpochToken(ExecutionTargetEpoch epoch)
    {
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(epoch.Value));

        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    /// <summary>Creates this generation's bootstrap folders without touching another's.</summary>
    /// <remarks>
    /// <para>
    /// Each generation gets its own host folders and its own guest paths. WSB holds a mapped folder
    /// root open for the lifetime of the Sandbox that mapped it, so reusing one fixed pair of names
    /// meant a new generation had to write into the folders an older one might still have mapped —
    /// which is unsafe the moment winapp adopts a guest whose previous manager mapped a folder of
    /// that same name and may still be running an agent out of it.
    /// </para>
    /// <para>
    /// Older generations are pruned on a best-effort basis. One that is still mapped cannot be
    /// deleted, and failing a command over that would trade a working Sandbox for an error.
    /// </para>
    /// </remarks>
    private BootstrapShare PrepareBootstrapDirectories(ExecutionTargetEpoch epoch)
    {
        var token = EpochToken(epoch);
        var root = directoryProvider.GetTargetRoot(Target, create: true).FullName;
        var bootstrap = TargetPathSafety.CombineInsideRoot(root, BootstrapFolderPrefix + token);
        var result = TargetPathSafety.CombineInsideRoot(root, ResultFolderPrefix + token);

        Directory.CreateDirectory(bootstrap);
        Directory.CreateDirectory(result);
        ClearDirectoryContents(result);

        PruneOldGenerations(root, token);

        return new BootstrapShare(
            bootstrap,
            result,
            GuestBootstrapPathPrefix + token,
            GuestResultPathPrefix + token);
    }

    /// <summary>Best-effort removal of bootstrap folders from generations that are long gone.</summary>
    /// <remarks>
    /// The two folder families are separated explicitly, because <c>bootstrap-result-</c> also starts
    /// with <c>bootstrap-</c>: a single prefix match would count result folders toward the read-only
    /// family's retention and could delete a live one.
    /// </remarks>
    private static void PruneOldGenerations(string root, string currentToken)
    {
        try
        {
            var directories = new DirectoryInfo(root).GetDirectories();

            var results = directories
                .Where(directory => directory.Name.StartsWith(ResultFolderPrefix, StringComparison.OrdinalIgnoreCase));

            var bootstraps = directories
                .Where(directory =>
                    directory.Name.StartsWith(BootstrapFolderPrefix, StringComparison.OrdinalIgnoreCase) &&
                    !directory.Name.StartsWith(ResultFolderPrefix, StringComparison.OrdinalIgnoreCase));

            PruneFamily(results, ResultFolderPrefix + currentToken);
            PruneFamily(bootstraps, BootstrapFolderPrefix + currentToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Housekeeping never fails a command.
        }
    }

    /// <summary>Keeps the current generation plus the most recent few, and drops the rest.</summary>
    private static void PruneFamily(IEnumerable<DirectoryInfo> family, string currentName)
    {
        var stale = family
            .Where(directory => !string.Equals(directory.Name, currentName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(directory => directory.LastWriteTimeUtc)
            .Skip(MaxRetainedBootstrapGenerations);

        foreach (var directory in stale)
        {
            try
            {
                directory.Delete(recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still mapped by a live Sandbox, or otherwise held open. It will be collected on a
                // later run once that instance is gone.
            }
        }
    }

    private static void ClearDirectoryContents(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.GetFiles(path))
        {
            File.Delete(file);
        }

        foreach (var directory in Directory.GetDirectories(path))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Publishes the host binary into the read-only bootstrap share and returns its hash.</summary>
    /// <remarks>
    /// Staging is skipped entirely when the bytes already match, which is the ordinary case: the
    /// same build reconnecting or repairing does not rewrite the share at all. Replacing the file is
    /// therefore only attempted when the host binary genuinely changed — and that is exactly when
    /// the running agent may still hold the old one open.
    /// </remarks>
    private async Task<string> StageBootstrapBinaryAsync(
        string bootstrapDirectory,
        CancellationToken cancellationToken)
    {
        var source = hostBinaryProvider.GetBinary();

        string expected;
        try
        {
            expected = await StageBootstrapFileAsync(
                source,
                TargetPathSafety.CombineInsideRoot(bootstrapDirectory, GuestAgentInstaller.BinaryName),
                cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            // The agent already serving this Sandbox is running from this exact file, so a newer
            // winapp cannot replace it in place. Reported as the actionable thing it is: the raw
            // exception surfaces as "IO_SharingViolation_NoFileName", which tells the user nothing
            // and looks like a winapp defect rather than a running-agent conflict.
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.AgentIncompatible,
                "A different version of winapp is already running the Windows Sandbox agent, " +
                "so this one could not replace it.",
                userAction: "Close Windows Sandbox, then run the command again to start a fresh agent.",
                nextCommand: new ExecutionTargetNextCommand
                {
                    Command = "wsb stop",

                    // Stopping discards whatever is running in the guest, so it stays the user's call.
                    Advisory = true,
                },
                innerException: ex);
        }

        var companion = new FileInfo(Path.Join(source.DirectoryName, SkiaCompanionName));
        if (companion.Exists)
        {
            try
            {
                await StageBootstrapFileAsync(
                    companion,
                    TargetPathSafety.CombineInsideRoot(bootstrapDirectory, SkiaCompanionName),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The companion is only needed for image encoding, and a locked one is byte-identical
                // to what a running agent already loaded. Failing the whole command over it would
                // turn a working Sandbox into an error.
            }
        }

        return expected;
    }

    /// <summary>Atomically stages and verifies one trusted host runtime file.</summary>
    private static async Task<string> StageBootstrapFileAsync(
        FileInfo source,
        string destination,
        CancellationToken cancellationToken)
    {
        var staged = $"{destination}.{Guid.NewGuid():N}.tmp";

        try
        {
            var expected = await GuestAgentIdentity
                .ComputeBinaryHashAsync(source.FullName, cancellationToken)
                .ConfigureAwait(false);

            if (File.Exists(destination))
            {
                var existing = await GuestAgentIdentity
                    .ComputeBinaryHashAsync(destination, cancellationToken)
                    .ConfigureAwait(false);

                if (string.Equals(expected, existing, StringComparison.OrdinalIgnoreCase))
                {
                    return expected;
                }
            }

            await using (var input = new FileStream(
                source.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                bufferSize: 128 * 1024,
                useAsync: true))
            await using (var output = new FileStream(
                staged,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                useAsync: true))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }

            var actual = await GuestAgentIdentity
                .ComputeBinaryHashAsync(staged, cancellationToken)
                .ConfigureAwait(false);

            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                throw ExecutionTargetException.Create(
                    ExecutionTargetErrorCodes.AgentUpgradeFailed,
                    $"The Windows Sandbox runtime file '{source.Name}' changed while it was staged.",
                    userAction: "Retry the command. If it keeps failing, reinstall winapp.",
                    context: new Dictionary<string, string>
                    {
                        ["expectedHash"] = expected,
                        ["actualHash"] = actual,
                    });
            }

            File.Move(staged, destination, overwrite: true);
            return expected;
        }
        catch
        {
            AtomicFile.DiscardStaged(staged);
            throw;
        }
    }

    /// <summary>
    /// Enables loose-package registration in a fresh guest (spec §"Framework support").
    /// </summary>
    /// <remarks>
    /// A fixed executable with a fixed argument string, exactly as the specification requires of the
    /// privileged setup winapp needs: it is not a general SYSTEM escape hatch and nothing about it
    /// is caller-influenced.
    /// <para>
    /// A failure is reported as a warning rather than failing the connection. Copying files and
    /// running commands in the guest do not need this, and refusing those because a registration
    /// prerequisite could not be set would turn one broken capability into all of them. A packaged
    /// run then fails with guest winapp's own Developer Mode message, which names the real problem.
    /// </para>
    /// </remarks>
    private async Task EnableGuestDevelopmentModeAsync(string instanceId, CancellationToken cancellationToken)
    {
        const string Command =
            @"reg.exe add ""HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock"" " +
            @"/f /v AllowDevelopmentWithoutDevLicense /t REG_DWORD /d 1";

        try
        {
            var exitCode = await cli.ExecuteAsync(
                instanceId,
                Command,
                workingDirectory: null,

                // The value lives under HKLM, which the interactive guest user cannot write
                // unelevated. This is the one operation that needs it.
                asSystem: true,
                cancellationToken).ConfigureAwait(false);

            if (exitCode != 0)
            {
                System.Diagnostics.Trace.TraceWarning(
                    "Could not enable Developer Mode in Windows Sandbox (exit code {0}); packaged runs may fail to register.",
                    exitCode);
            }
        }

        catch (ExecutionTargetException ex)
        {
            System.Diagnostics.Trace.TraceWarning(
                "Could not enable Developer Mode in Windows Sandbox: {0}", ex.Message);
        }
    }

    /// <summary>Allows inbound TCP only to the staged guest agent on its pre-assigned port.</summary>
    /// <remarks>
    /// <para>
    /// Windows Sandbox blocks the host-to-guest connection by default. This is the second and only
    /// other privileged bootstrap operation: the executable, direction, protocol, action, and
    /// profile are constants; the sole dynamic value is the integer port the host chose.
    /// Authentication and encryption still gate every request after the socket opens.
    /// </para>
    /// <para>
    /// <b>This must run before the agent starts listening.</b> Windows raises the "Windows Firewall
    /// has blocked some features of this app" consent dialog at the moment a program binds a
    /// listening socket with no matching rule — so a rule created afterwards, however correct,
    /// arrives too late to prevent the prompt and leaves the user staring at a dialog inside the
    /// Sandbox window. That ordering is why the port is chosen by the host rather than by binding
    /// port 0 and reading back what the agent got.
    /// </para>
    /// <para>
    /// The rule is scoped to the agent program <em>and</em> the single port it was told to use, so
    /// nothing else in the guest gains inbound reachability. Existing rules for that exact program
    /// path are removed first, which is what lets the <em>same</em> generation be repaired onto a
    /// fresh port without leaving its previous one open.
    /// </para>
    /// <para>
    /// The program path is per generation, so this deliberately cannot see, and cannot remove, a
    /// rule belonging to a different generation — including one created by a second winapp whose
    /// state root was redirected, which under the old fixed path would have had its live agent's
    /// rule deleted underneath it. A rule left behind by a generation that is gone names a program
    /// path that no longer exists, lives only in the volatile <c>ActiveStore</c>, and disappears
    /// with the Sandbox, so it authorises nothing.
    /// </para>
    /// </remarks>
    private async Task AllowGuestAgentConnectionAsync(
        string instanceId,
        string guestBootstrapPath,
        int port,
        CancellationToken cancellationToken)
    {
        if (port is <= 0 or > IPEndPoint.MaxPort)
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.TransportFailed,
                "The Windows Sandbox agent was assigned an invalid TCP port.",
                userAction: "Close Windows Sandbox, then retry.",
                context: new Dictionary<string, string>
                {
                    ["port"] = port.ToString(CultureInfo.InvariantCulture),
                });
        }

        var portText = port.ToString(CultureInfo.InvariantCulture);
        var agentPath = $@"{guestBootstrapPath}\{GuestAgentInstaller.BinaryName}";
        var command =
            @"powershell.exe -NoProfile -NonInteractive -Command " +
            $@"""$agent='{agentPath}'; " +

            // Any existing rule for this exact program path is removed first, block or allow: a
            // block rule would defeat the new allow, and a stale allow from an earlier attempt at
            // this same generation would leave a port open that is no longer in use.
            @"$existing=Get-NetFirewallRule -PolicyStore ActiveStore -ErrorAction SilentlyContinue | Where-Object { " +
            @"($_ | Get-NetFirewallApplicationFilter -ErrorAction SilentlyContinue).Program -eq $agent }; " +
            @"foreach ($rule in $existing) { " +
            @"Remove-NetFirewallRule -Name $rule.Name -PolicyStore ActiveStore -ErrorAction SilentlyContinue; " +
            @"Remove-NetFirewallRule -Name $rule.Name -PolicyStore PersistentStore -ErrorAction SilentlyContinue }; " +
            $@"New-NetFirewallRule -DisplayName 'WinApp Sandbox Agent {portText}' " +
            $@"-Direction Inbound -Action Allow -Protocol TCP -LocalPort {portText} -Program $agent " +
            @"-Profile Any -EdgeTraversalPolicy Allow -PolicyStore ActiveStore -ErrorAction Stop | Out-Null""";

        var exitCode = await cli.ExecuteAsync(
            instanceId,
            command,
            workingDirectory: null,
            asSystem: true,
            cancellationToken).ConfigureAwait(false);

        if (exitCode != 0)
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.TransportFailed,
                "Windows Sandbox could not allow the authenticated guest-agent connection.",
                userAction: "Close Windows Sandbox, then retry.",
                context: new Dictionary<string, string>
                {
                    ["exitCode"] = exitCode.ToString(CultureInfo.InvariantCulture),
                    ["port"] = port.ToString(CultureInfo.InvariantCulture),
                });
        }
    }

    /// <summary>
    /// Starts the guest agent through <c>wsb exec</c>, the one fixed bootstrap operation.
    /// </summary>
    /// <remarks>
    /// The agent runs from the read-only share on first launch. It copies itself into guest-local
    /// storage before serving, so the host share is not held open for the life of the Sandbox.
    /// </remarks>
    private async Task LaunchAgentAsync(
        string instanceId,
        BootstrapShare bootstrap,
        CancellationToken cancellationToken)
    {
        var command =
            $"\"{bootstrap.GuestBootstrap}\\{GuestAgentInstaller.BinaryName}\" {GuestAgentCommandNames.Verb} " +
            $"--bootstrap-dir \"{bootstrap.GuestBootstrap}\" --result-dir \"{bootstrap.GuestResult}\"";

        await cli.LaunchAgentAsync(instanceId, command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts the agent, waiting for a just-launched client to make the desktop input-ready.
    /// </summary>
    /// <remarks>
    /// The client process is necessarily launched without waiting for it to exit, and its window can
    /// appear before the guest input desktop is ready. An agent started in that interval reports
    /// <c>NoInputDesktop</c> and exits without accepting anything, so repeating that bootstrap is
    /// safe — <em>while a client winapp just launched is still coming up</em>.
    /// <para>
    /// When no client was launched, the same report means something entirely different: the guest
    /// has a login session but nobody is attached to it, which is what a closed Sandbox window looks
    /// like. Retrying that would spin until the heartbeat deadline and then blame the wrong thing,
    /// so it is reported immediately as the reconnectable condition it is.
    /// </para>
    /// No other failure is retried: once a command can be dispatched, its result is authoritative
    /// and repeating it could duplicate a side effect.
    /// </remarks>
    /// <summary>Connects the interactive client and parks the window this connect created.</summary>
    /// <remarks>
    /// The connect is held open across the placement so its process ID cannot be recycled while the
    /// controller is matching client windows against it. Which window belongs to this connect is
    /// settled by parentage, so a client another caller opened at the same moment is never a
    /// candidate — and when the evidence is missing, nothing is parked and nothing is recorded.
    /// </remarks>
    private async Task ConnectClientAsync(
        string instanceId,
        CancellationToken cancellationToken)
    {
        var windowSnapshot = windowController.Capture();
        using var placementCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        SandboxConnectAttempt? attempt = null;
        var connectCompleted = false;

        try
        {
            attempt = await cli.ConnectAsync(
                instanceId,
                launched =>
                {
                    attempt = launched;
                    windowController.ObserveConnect(
                        windowSnapshot, launched, placementCancellation.Token);
                },
                cancellationToken).ConfigureAwait(false);
            connectCompleted = true;
        }
        finally
        {
            if (!connectCompleted)
            {
                placementCancellation.Cancel();
            }

            if (attempt is not null)
            {
                try
                {
                    var placed = await windowController
                        .PlaceConnectedClientAsync(
                            windowSnapshot,
                            attempt,
                            connectCompleted ? cancellationToken : CancellationToken.None)
                        .ConfigureAwait(false);

                    if (placed is not null)
                    {
                        _client = placed;
                        RememberClientWindow(placed);
                    }
                }
                catch (OperationCanceledException) when (!connectCompleted)
                {
                    // Cancellation before placement leaves no hidden client to remember.
                }
                finally
                {
                    placementCancellation.Cancel();
                    attempt.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// Starts the agent, connecting a client once if the guest turns out not to have a usable one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The client process is launched without waiting for it to exit, and its window can appear
    /// before the guest input desktop is ready. An agent started in that interval reports
    /// <c>NoInputDesktop</c> and exits without accepting anything, so repeating that bootstrap is
    /// safe while a client winapp launched is still coming up.
    /// </para>
    /// <para>
    /// When no client was launched, the same report is evidence rather than timing: the guest has a
    /// login session — so the session probe said so — but nothing is attached to it, which is what a
    /// closed Sandbox window looks like. <c>--on sandbox</c> is consent to make the Sandbox usable, so
    /// winapp reconnects rather than stopping to ask. That happens <b>exactly once</b>, driven by
    /// the agent's own failure rather than by a guess, which is what keeps a healthy attached client
    /// from ever being duplicated: a guest whose client is working does not produce this report.
    /// </para>
    /// <para>
    /// After that one reconnect the wait between attempts is deliberately short. A client that has
    /// just been attached becomes input-ready in seconds, and continuing to the full heartbeat
    /// deadline would turn a recoverable condition into a three-minute silence before saying the
    /// same thing. That bound governs the retry loop only: a guest that goes completely silent
    /// rather than repeating its refusal is still held by
    /// <see cref="WaitForHeartbeatAsync"/>'s own deadline, which this cannot cut short.
    /// </para>
    /// </remarks>
    private async Task<GuestAgentHeartbeat> LaunchReadyAgentAsync(
        string instanceId,
        BootstrapShare bootstrap,
        ExecutionTargetEpoch epoch,
        bool clientWasLaunched,
        CancellationToken cancellationToken)
    {
        var deadline = UtcNow() + HeartbeatTimeout;
        var clientAvailable = clientWasLaunched;

        // A client is connected at most once here, and only when the bootstrap did not already do
        // it. Without that, an expired deadline on the ordinary path would fall through and attach
        // a second client.
        var mayReconnect = !clientWasLaunched;

        while (true)
        {
            TryClearResultFolder(bootstrap.HostResult);

            try
            {
                await LaunchAgentAsync(instanceId, bootstrap, cancellationToken).ConfigureAwait(false);
            }
            catch (ExecutionTargetException ex) when (
                ex.Error.Code == ExecutionTargetErrorCodes.TransportFailed &&
                UtcNow() < deadline)
            {
                await Delay(AgentLaunchRetryDelay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                return await WaitForHeartbeatAsync(bootstrap.HostResult, epoch, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ExecutionTargetException ex) when (IsNoInputDesktop(ex))
            {
                if (clientAvailable && UtcNow() < deadline)
                {
                    await Delay(AgentLaunchRetryDelay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (mayReconnect)
                {
                    mayReconnect = false;

                    await ConnectClientAsync(instanceId, cancellationToken)
                        .ConfigureAwait(false);

                    clientAvailable = true;
                    deadline = UtcNow() + ClientReconnectReadyTimeout;

                    await Delay(AgentLaunchRetryDelay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw ExecutionTargetException.Create(
                    ExecutionTargetErrorCodes.InputNotReady,
                    "The Windows Sandbox guest has no input desktop, so real input and screen capture "
                    + "are unavailable.",
                    userAction: "Reconnect the Sandbox window, then retry.",
                    context: new Dictionary<string, string> { ["sandboxId"] = instanceId },
                    nextCommand: new ExecutionTargetNextCommand
                    {
                        Command = $"wsb connect --id {instanceId}",

                        // Reconnecting changes what is on screen, so it needs a user decision.
                        Advisory = true,
                    },
                    innerException: ex);
            }
        }
    }

    /// <summary>Whether the agent refused because no input desktop is available.</summary>
    private static bool IsNoInputDesktop(ExecutionTargetException exception) =>
        exception.Error.Code == ExecutionTargetErrorCodes.NoInteractiveSession &&
        exception.Error.Context?.GetValueOrDefault("reason") == GuestReadinessFailure.NoInputDesktop.ToString();

    /// <summary>
    /// Waits for the agent's readiness heartbeat, reporting its own diagnostics when it refuses.
    /// </summary>
    /// <remarks>
    /// The agent publishes a heartbeat even when it is <em>not</em> ready, so a refusal to serve
    /// surfaces the exact reason — session 0, no input desktop — rather than as a timeout on
    /// silence. That is the whole reason the result folder exists: <c>wsb exec</c> returns only an
    /// exit code and can never carry it.
    /// </remarks>
    internal static async Task<GuestAgentHeartbeat> WaitForHeartbeatAsync(
        string resultDirectory,
        ExecutionTargetEpoch epoch,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + HeartbeatTimeout;
        var path = Path.Join(resultDirectory, HeartbeatFileName);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureResultFolderWithinLimits(resultDirectory);

            if (TryReadHeartbeat(path) is { } heartbeat &&
                string.Equals(heartbeat.TargetEpoch, epoch.Value, StringComparison.Ordinal))
            {
                if (!heartbeat.Ready)
                {
                    throw ExecutionTargetException.Create(
                        ExecutionTargetErrorCodes.NoInteractiveSession,
                        "The Windows Sandbox agent started but is not able to serve commands.",
                        userAction: "Retry the command so winapp restarts the agent in the interactive session.",
                        context: new Dictionary<string, string>
                        {
                            ["reason"] = heartbeat.NotReadyReason ?? "unknown",
                        });
                }

                return heartbeat;
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw ExecutionTargetException.Create(
                    ExecutionTargetErrorCodes.StartFailed,
                    "The Windows Sandbox agent did not report ready in time.",
                    userAction: "Retry the command. If it keeps failing, close Windows Sandbox and try again.",
                    context: ReadStartupDiagnostics(resultDirectory));
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>File the agent publishes its heartbeat to.</summary>
    internal const string HeartbeatFileName = "heartbeat.json";

    /// <summary>File the agent writes startup stdout and stderr to.</summary>
    internal const string StartupLogFileName = "startup.log";

    private static GuestAgentHeartbeat? TryReadHeartbeat(string path)
    {
        try
        {
            return File.Exists(path) ? GuestAgentHeartbeat.TryParse(File.ReadAllText(path)) : null;
        }
        catch (IOException)
        {
            // The agent is mid-write. The next poll sees the complete file.
            return null;
        }
    }

    /// <summary>
    /// Refuses a result folder that has grown beyond its fixed limits.
    /// </summary>
    /// <remarks>
    /// This folder is the only guest-writable path the host reads, so it is treated as untrusted
    /// input. Bounding file count and total size stops a co-resident guest process from filling the
    /// host's disk through it, or from making the host read an unbounded amount while polling.
    /// </remarks>
    internal static void EnsureResultFolderWithinLimits(string resultDirectory)
    {
        if (!Directory.Exists(resultDirectory))
        {
            return;
        }

        var files = new DirectoryInfo(resultDirectory).GetFiles("*", SearchOption.AllDirectories);
        if (files.Length <= MaxResultFiles && files.Sum(f => f.Length) <= MaxResultBytes)
        {
            return;
        }

        throw ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.StartFailed,
            "The Windows Sandbox startup folder exceeded its size limit and was not read.",
            userAction: "Close Windows Sandbox, then retry.",
            context: new Dictionary<string, string>
            {
                ["fileCount"] = files.Length.ToString(CultureInfo.InvariantCulture),
            });
    }

    /// <summary>Reads whatever the agent managed to say before it failed.</summary>
    private static Dictionary<string, string> ReadStartupDiagnostics(string resultDirectory)
    {
        var context = new Dictionary<string, string>(StringComparer.Ordinal);
        var log = Path.Join(resultDirectory, StartupLogFileName);

        try
        {
            if (File.Exists(log))
            {
                var text = File.ReadAllText(log).Trim();

                // Bounded: this is untrusted guest output going into a host error envelope.
                context["guestOutput"] = text.Length > 2000 ? text[..2000] : text;
            }
        }
        catch (IOException)
        {
            // No diagnostics available; the timeout message still stands on its own.
        }

        return context;
    }

    private static void TryClearResultFolder(string resultDirectory)
    {
        try
        {
            ClearDirectoryContents(resultDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The guest still holds a staged diagnostic open. A stale report is epoch-checked before
            // use, so cleanup failure must not fail a connection that already succeeded.
            System.Diagnostics.Trace.TraceWarning(
                "Could not clear the Sandbox bootstrap result folder '{0}': {1}", resultDirectory, ex.Message);
        }
    }
}
