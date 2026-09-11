// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Net.Sockets;
using System.Text;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Services;

namespace WinApp.Cli.ExecutionTargets.GuestAgent;

/// <summary>
/// The persistent guest agent's lifetime: install itself locally, verify its session, publish a
/// heartbeat, then serve host channels.
/// </summary>
/// <remarks>
/// Separated from the CLI verb so the sequence — including the refusal paths, which are the ones
/// that matter — is testable without a command line or a Sandbox.
/// <para>
/// The heartbeat is published even when the agent is <em>not</em> ready. That is deliberate: the
/// bootstrap channel returns only an exit code, so an agent that stayed silent when it refused to
/// serve would surface to the user as a timeout instead of "the Sandbox window is disconnected".
/// </para>
/// <para>
/// Concurrent channels are the acceptor's business, not this class's. What stays here is the
/// lifetime that has to happen exactly once per boot: containment, material, identity, readiness,
/// the listener, and the heartbeat.
/// </para>
/// </remarks>
internal sealed class GuestAgentRunner(
    IGuestSessionProbe sessionProbe,
    IGuestProcessHostFactory processes,
    IAppLauncherService? appLauncher = null,
    IPackageRegistrationService? packageRegistration = null)
{
    /// <summary>Guest-local root the agent installs itself and managed storage under.</summary>
    internal const string DefaultManagedRoot = @"C:\WinApp";

    /// <summary>Runs the agent until the host disconnects or cancellation.</summary>
    /// <param name="bootstrapDirectory">Read-only folder holding this boot's connection material.</param>
    /// <param name="resultDirectory">Bounded writable folder for readiness output and diagnostics.</param>
    /// <param name="managedRoot">Guest-local root for deployments, artifacts, runtimes, and work.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Process exit code: 0 when the host disconnected cleanly.</returns>
    public async Task<int> RunAsync(
        string bootstrapDirectory,
        string resultDirectory,
        string managedRoot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bootstrapDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(resultDirectory);

        Directory.CreateDirectory(resultDirectory);

        // Before any child is started: every descendant of a job member joins that job at creation
        // time, so this closes the window that assigning a child after Process.Start cannot.
        GuestJobObject.EnsureAgentContainment();

        var material = ReadMaterial(bootstrapDirectory, resultDirectory);
        if (material is null)
        {
            return 1;
        }

        var identity = await GuestAgentIdentity
            .ForCurrentProcessAsync(cancellationToken)
            .ConfigureAwait(false);

        var epoch = new ExecutionTargetEpoch(material.TargetEpoch);
        var session = sessionProbe.Probe();
        var readiness = GuestAgentReadiness.Evaluate(session);

        TcpListener listener;
        int port;

        try
        {
            (listener, port) = GuestTcpTransport.Listen(material.Port);
        }
        catch (SocketException ex)
        {
            WriteStartupLog(resultDirectory, $"The agent could not listen for the host: {ex.Message}");
            return 1;
        }

        // Published before the first accept, and published whether or not the agent is ready, so
        // the host learns the port and the exact refusal reason from the same file.
        PublishHeartbeat(resultDirectory, identity, readiness, epoch, port);

        if (readiness != GuestReadinessFailure.None)
        {
            // Serving would mean accepting UI commands this session cannot perform and reporting
            // input that was never delivered, which the specification forbids outright.
            WriteStartupLog(resultDirectory, $"The agent is not ready to serve: {readiness}.");
            listener.Stop();
            return 1;
        }

        var files = new GuestFileService(managedRoot);

        // Every collaborator below is shared across concurrent channels, so each is either immutable
        // or independently safe: the file service holds only a resolved root, the session probe
        // re-reads the live session on every call, the process factory builds a self-contained host
        // per operation, and the identity is a record. Per-connection state — operations, transfers,
        // standard input, cancellation, and session keys — lives entirely inside each server.
        var acceptor = new GuestConnectionAcceptor(
            new GuestTcpConnectionSource(listener, material),
            (transport, refusal) => new GuestCommandServer(
                transport,
                epoch,
                processes,
                sessionProbe,
                identity,
                files,

                // The agent is the only party that can assert which binary is guest winapp:
                // it is the one running it.
                Environment.ProcessPath,
                appLauncher,
                packageRegistration)
            {
                AdmissionRefusal = refusal,
            },
            connectionClosed: () => PublishHeartbeat(
                resultDirectory,
                identity,

                // Re-probed rather than cached: the user may have closed the Sandbox window while
                // that channel was open.
                GuestAgentReadiness.Evaluate(sessionProbe.Probe()),
                epoch,
                port));

        try
        {
            await acceptor.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        catch (ExecutionTargetException ex)
        {
            WriteStartupLog(resultDirectory, $"The agent stopped serving: {ex.Error.Message}");
            return 1;
        }
        finally
        {
            listener.Stop();
        }

        return 0;
    }

    /// <summary>Reads this boot's connection material, reporting why when it cannot.</summary>
    private static GuestBootstrapMaterial? ReadMaterial(string bootstrapDirectory, string resultDirectory)
    {
        var path = Path.Join(bootstrapDirectory, GuestBootstrapMaterial.FileName);

        try
        {
            var material = GuestBootstrapMaterial.TryParse(File.ReadAllText(path));

            if (material is null)
            {
                WriteStartupLog(resultDirectory, "The connection material is malformed or from an unknown schema.");
            }

            return material;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WriteStartupLog(resultDirectory, $"The connection material could not be read: {ex.Message}");
            return null;
        }
    }

    private static void PublishHeartbeat(
        string resultDirectory,
        GuestAgentIdentity identity,
        GuestReadinessFailure readiness,
        ExecutionTargetEpoch epoch,
        int port)
    {
        var heartbeat = GuestAgentHeartbeat.Create(identity, readiness, epoch, port, DateTimeOffset.UtcNow);
        var path = Path.Join(resultDirectory, WindowsSandbox.WindowsSandboxBackend.HeartbeatFileName);

        try
        {
            // Written through a temporary and moved, so the host never polls a half-written file
            // and concludes the agent published nothing.
            var temporary = $"{path}.{Guid.NewGuid():n}.tmp";
            File.WriteAllText(temporary, heartbeat.ToJson());
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The host's own timeout reports this; there is nowhere else to record it from inside
            // the guest, since this folder is the only writable channel out.
            System.Diagnostics.Trace.TraceWarning("Could not publish the agent heartbeat: {0}", ex.Message);
        }
    }

    /// <summary>Appends a line the host surfaces when the agent never became usable.</summary>
    private static void WriteStartupLog(string resultDirectory, string message)
    {
        try
        {
            File.AppendAllText(
                Path.Join(resultDirectory, WindowsSandbox.WindowsSandboxBackend.StartupLogFileName),
                $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Diagnostics are best-effort. Failing to write them must not change the exit code the
            // host reads, which is the only other signal it has.
            System.Diagnostics.Trace.TraceWarning("Could not write agent startup diagnostics: {0}", ex.Message);
        }
    }
}
