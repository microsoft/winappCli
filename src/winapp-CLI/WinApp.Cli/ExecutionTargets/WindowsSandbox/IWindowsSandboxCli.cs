// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace WinApp.Cli.ExecutionTargets.WindowsSandbox;

/// <summary>Whether a guest already has an interactive session to run things in.</summary>
internal enum GuestSessionAvailability
{
    /// <summary>An <c>ExistingLogin</c> command ran, so a client session is already established.</summary>
    Ready,

    /// <summary>
    /// The guest reported <c>ERROR_NO_SUCH_LOGON_SESSION</c>: nobody has connected a client yet.
    /// </summary>
    NoLoginSession,

    /// <summary>The probe could not answer, so nothing may be concluded from it.</summary>
    Unknown,
}

/// <summary>
/// Typed wrapper over the <c>wsb.exe</c> command line.
/// </summary>
/// <remarks>
/// This is the only seam through which winapp touches Windows Sandbox. Keeping it behind an
/// interface is what lets the lifecycle rules — singleton ownership, unmanaged refusal, teardown
/// waiting, epoch recovery — be exercised deterministically without starting a real Sandbox, and it
/// keeps <c>wsb</c> knowledge out of every layer above the backend.
/// </remarks>
internal interface IWindowsSandboxCli
{
    /// <summary>Whether a trusted <c>wsb.exe</c> has been resolved on this host.</summary>
    /// <remarks>
    /// Re-evaluated rather than cached for the life of the process, because prerequisite setup can
    /// make an alias appear part-way through a single command. A value fixed at first use would
    /// report the host as unusable for the rest of an invocation that had just finished making it
    /// usable.
    /// </remarks>
    bool IsAvailable { get; }

    /// <summary>
    /// Binds this client to the exact <c>wsb.exe</c> the readiness probe validated.
    /// </summary>
    /// <remarks>
    /// Setup already resolved and executed this path to prove it answers, so reusing it here avoids
    /// a second resolution that could pick a different file.
    /// </remarks>
    void UseExecutable(string executablePath);

    /// <summary>Lists the IDs of every running Sandbox, managed or not.</summary>
    Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Starts a Sandbox under the caller's own instance ID and returns the ID it reports.
    /// </summary>
    /// <remarks>
    /// The caller assigns the ID so that a start which fails <em>after</em> creating an instance can
    /// still be reconciled: without one, a later command could only guess which listed instance was
    /// the one it had just tried to create, and guessing from a list delta would attribute someone
    /// else's Sandbox to winapp.
    /// </remarks>
    Task<string> StartAsync(string instanceId, string? configuration, CancellationToken cancellationToken);

    /// <summary>
    /// Terminates the Sandbox with the given ID.
    /// </summary>
    /// <remarks>
    /// Deliberately has no production caller. <c>--on sandbox</c> reuses and takes over instances but
    /// never ends one, because a running Sandbox may hold work winapp cannot see — so stopping is
    /// offered to the user as advisory guidance and exercised by tests, and wiring it into a
    /// failure or cleanup path would break the guarantee the rest of this type is built on.
    /// </remarks>
    Task StopAsync(string id, CancellationToken cancellationToken);

    /// <summary>Returns the guest's IPv4 address.</summary>
    Task<string> GetIpAddressAsync(string id, CancellationToken cancellationToken);

    /// <summary>
    /// Whether the instance resolves to a usable guest, without changing anything about it.
    /// </summary>
    /// <remarks>
    /// A listed ID is not proof that a Sandbox can be used: an instance that is still coming up, or
    /// on its way out, is listed exactly like a healthy one. Resolving its address is the cheapest
    /// question whose answer requires the guest to actually be there, which is what makes it the
    /// gate before anything winapp does would change guest state.
    /// </remarks>
    Task<bool> IsResolvableAsync(string id, CancellationToken cancellationToken);

    /// <summary>Shares a host folder into the guest.</summary>
    Task ShareFolderAsync(
        string id,
        string hostPath,
        string sandboxPath,
        bool allowWrite,
        CancellationToken cancellationToken);

    /// <summary>
    /// Starts the interactive remote session and reports its launcher before observing the bounded
    /// immediate-failure window.
    /// </summary>
    /// <returns>
    /// The launched connect, which the callback receives immediately and the caller must dispose once
    /// it has finished attributing client windows to it. The callback owns that attempt even when the
    /// bounded failure watch later throws or is cancelled, so any already-placed client can still be
    /// persisted before the error propagates.
    /// </returns>
    Task<SandboxConnectAttempt> ConnectAsync(
        string id,
        Action<SandboxConnectAttempt> onLaunched,
        CancellationToken cancellationToken);

    /// <summary>
    /// Runs one fixed bootstrap command in the guest and returns the <em>guest</em> exit code.
    /// </summary>
    /// <remarks>
    /// <c>wsb exec</c> takes the command as a single string and never relays the guest's stdout or
    /// stderr. That is why it is used solely for fixed bootstrap operations: real work needs
    /// argument fidelity and streaming, which only the authenticated channel provides.
    /// <para>
    /// The returned value is the guest process's own exit code, read from <c>--raw</c> output.
    /// <c>wsb</c>'s exit code is <b>not</b> it: a guest command that fails leaves <c>wsb</c> exiting
    /// 0, so returning that would report every failed privileged bootstrap step as a success.
    /// </para>
    /// </remarks>
    /// <exception cref="ExecutionTargetException">
    /// The command could not be dispatched at all, or <c>wsb</c> reported no guest exit code.
    /// </exception>
    Task<int> ExecuteAsync(
        string id,
        string command,
        string? workingDirectory,
        bool asSystem,
        CancellationToken cancellationToken);

    /// <summary>
    /// Asks, without changing anything, whether the guest has an interactive login session.
    /// </summary>
    /// <remarks>
    /// The one cheap question that distinguishes a guest whose client is already attached from one
    /// that has never had a session. It exists so a Sandbox winapp took over is not handed a second
    /// client it does not need — see <c>WindowsSandboxBackend</c>.
    /// </remarks>
    Task<GuestSessionAvailability> ProbeInteractiveSessionAsync(string id, CancellationToken cancellationToken);

    /// <summary>
    /// Launches the fixed persistent guest-agent bootstrap command without waiting for it to exit.
    /// </summary>
    /// <remarks>
    /// This is separate from <see cref="ExecuteAsync"/> because a healthy agent intentionally remains
    /// running. The agent heartbeat, not the lifetime of <c>wsb exec</c>, proves successful dispatch.
    /// </remarks>
    Task LaunchAgentAsync(string id, string command, CancellationToken cancellationToken);
}

/// <summary>One entry of <c>wsb list --raw</c>.</summary>
internal sealed class WsbEnvironment
{
    /// <summary>The Sandbox instance ID.</summary>
    public string? Id { get; init; }
}

/// <summary>Payload of <c>wsb list --raw</c> and <c>wsb start --raw</c>.</summary>
internal sealed class WsbEnvironmentList
{
    /// <summary>Running environments, or the one just created.</summary>
    public List<WsbEnvironment>? WindowsSandboxEnvironments { get; init; }

    /// <summary>Some verbs report a bare ID at the root instead of a list.</summary>
    public string? Id { get; init; }
}

/// <summary>One entry of <c>wsb ip --raw</c>.</summary>
internal sealed class WsbNetwork
{
    /// <summary>The guest's IPv4 address.</summary>
    public string? IpV4Address { get; init; }
}

/// <summary>Payload of <c>wsb ip --raw</c>.</summary>
internal sealed class WsbNetworkList
{
    /// <summary>Guest networks.</summary>
    public List<WsbNetwork>? Networks { get; init; }
}

/// <summary>
/// Payload of <c>wsb exec --raw</c>.
/// </summary>
/// <remarks>
/// Measured on a live Sandbox: a dispatched command makes <c>wsb</c> itself exit 0 and print
/// <c>{ "ExitCode": N }</c>, where <c>N</c> is the <em>guest</em> process's exit code. A command
/// that could not be dispatched at all makes <c>wsb</c> exit with an HRESULT and write to standard
/// error instead, printing no JSON. Those are different failures with different meanings, and
/// reading only <c>wsb</c>'s own exit code reports every failed guest command as a success.
/// </remarks>
internal sealed class WsbExecResult
{
    /// <summary>The exit code of the process that ran inside the guest.</summary>
    public int? ExitCode { get; init; }
}

/// <summary>
/// Source-generated serializer context for <c>wsb --raw</c> payloads.
/// </summary>
/// <remarks>
/// <c>wsb</c> emits PascalCase property names, so this context deliberately does not apply the
/// camelCase policy used for winapp's own output.
/// </remarks>
[JsonSerializable(typeof(WsbEnvironmentList))]
[JsonSerializable(typeof(WsbNetworkList))]
[JsonSerializable(typeof(WsbExecResult))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class WindowsSandboxCliJsonContext : JsonSerializerContext
{
}
