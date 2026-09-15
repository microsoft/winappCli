// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.Tests;

/// <summary>
/// Handshake failure classification over real loopback sockets.
/// </summary>
/// <remarks>
/// The in-memory duplex pair the rest of the suite uses cannot produce these outcomes. Closing one
/// of its ends is always a clean end of stream, whereas a real socket closed with unread data in its
/// receive buffer sends RST, which surfaces as a <see cref="SocketException"/> rather than an
/// <see cref="EndOfStreamException"/>. Dropping a connection at the tracked ceiling does exactly
/// that — it closes without ever reading the hello the host has already sent — so only a real socket
/// exercises the shape that actually occurs.
/// <para>
/// What the classification decides is not cosmetic. A peer that accepted and then closed was alive,
/// which is what an agent at its channel ceiling is; anything else means the agent is gone. The
/// first must never cause a repair, because repairing stages and relaunches an agent underneath the
/// channels a healthy one is still serving.
/// </para>
/// </remarks>
[TestClass]
public class GuestHandshakeSocketTests
{
    private const string TargetId = "sandbox-default-6b0d287c0c51bc40";
    private const string Loopback = "127.0.0.1";

    private static readonly TimeSpan Promptly = TimeSpan.FromSeconds(20);
    private static readonly ExecutionTargetEpoch Epoch = ExecutionTargetEpoch.Create("sandbox-1", "nonce-a");

    public TestContext TestContext { get; set; } = null!;

    private static byte[] NewKey() => RandomNumberGenerator.GetBytes(GuestProtocol.PreSharedKeySize);

    /// <summary>
    /// Binds a real listener that only loopback can reach.
    /// </summary>
    /// <remarks>
    /// Every listener in this file goes through here, and the assertion is the point of it. Binding
    /// every interface — which is what the guest agent correctly does, and what these tests used to
    /// inherit by calling the agent's own overload — makes Windows raise a Firewall consent prompt
    /// for the test executable and leave "Query User" rules behind on the developer's machine. A
    /// test must never do that.
    /// <para>
    /// Loopback is still a real socket, so resets, RST-on-close, and the rest of the behaviour these
    /// tests exist to cover are unaffected; it is only reachability that narrows.
    /// </para>
    /// </remarks>
    private static (TcpListener Listener, int Port) ListenOnLoopback()
    {
        var (listener, port) = GuestTcpTransport.Listen(requestedPort: 0, IPAddress.Loopback);

        Assert.AreEqual(
            IPAddress.Loopback,
            ((IPEndPoint)listener.LocalEndpoint).Address,
            "A test listener must bind loopback only, or running the suite prompts for firewall access.");

        return (listener, port);
    }

    private static GuestBootstrapMaterial Material(byte[] key, int port) => new()
    {
        SchemaVersion = GuestBootstrapMaterial.CurrentSchemaVersion,
        TargetId = TargetId,
        TargetEpoch = Epoch.Value,
        PreSharedKey = Convert.ToBase64String(key),
        Port = port,
    };

    [TestMethod]
    public async Task PeerThatAcceptsThenResets_IsClassifiedAsClosedDuringHandshake()
    {
        var key = NewKey();
        var (listener, port) = ListenOnLoopback();
        using var _ = listener;

        // Exactly what GuestConnectionAcceptor does past its tracked ceiling: accept, then close
        // without ever reading. The host's hello is already in the receive buffer, so Windows
        // answers with RST rather than FIN.
        var dropping = Task.Run(async () =>
        {
            var client = await listener.AcceptTcpClientAsync(TestContext.CancellationToken);
            client.Client.LingerState = new LingerOption(enable: true, seconds: 0);
            client.Dispose();
        });

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => GuestTcpTransport.ConnectAsync(Loopback, Material(key, port), TestContext.CancellationToken)
                .WaitAsync(Promptly, TestContext.CancellationToken));

        await dropping;

        // Coded, not a raw IOException. An uncoded failure would escape the caller that decides
        // between reporting and repairing, and reach the user as an unclassified I/O error.
        Assert.AreEqual(ExecutionTargetErrorCodes.TransportFailed, failure.Error.Code);
        Assert.IsTrue(
            failure.Error.Context?.ContainsKey(GuestSecureChannel.ClosedDuringHandshakeKey) is true,
            "A peer that accepted and then reset the connection must be recognised as having closed it.");

        // The original cause survives classification, so diagnostics still say what happened.
        Assert.IsNotNull(failure.InnerException);
    }

    [TestMethod]
    public async Task PeerThatAcceptsThenClosesCleanly_IsClassifiedAsClosedDuringHandshake()
    {
        var key = NewKey();
        var (listener, port) = ListenOnLoopback();
        using var _ = listener;

        // The other shape of the same event: the peer drains the hello first, so the close is a
        // clean FIN and surfaces as end of stream. Both must classify the same way.
        var closing = Task.Run(async () =>
        {
            var client = await listener.AcceptTcpClientAsync(TestContext.CancellationToken);
            var buffer = new byte[GuestProtocol.HelloSize];
            await client.GetStream().ReadExactlyAsync(buffer, TestContext.CancellationToken);
            client.Dispose();
        });

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => GuestTcpTransport.ConnectAsync(Loopback, Material(key, port), TestContext.CancellationToken)
                .WaitAsync(Promptly, TestContext.CancellationToken));

        await closing;

        Assert.IsTrue(
            failure.Error.Context?.ContainsKey(GuestSecureChannel.ClosedDuringHandshakeKey),
            "A clean close during the handshake must classify the same as a reset.");
    }

    [TestMethod]
    public async Task AgentResetMidHandshakeThenGone_IsDiagnosedAsDeadRatherThanBusy()
    {
        // An agent killed mid-handshake resets the connection it had accepted — indistinguishable,
        // on that reset alone, from one dropping a connection at its tracked ceiling. This is the
        // case that must still repair, and what separates it is that nothing answers afterwards.
        var key = NewKey();
        var (listener, port) = ListenOnLoopback();

        var killed = Task.Run(async () =>
        {
            var client = await listener.AcceptTcpClientAsync(TestContext.CancellationToken);
            client.Client.LingerState = new LingerOption(enable: true, seconds: 0);
            client.Dispose();

            // The agent process is gone, so its listener goes with it.
            listener.Dispose();
        });

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => GuestTcpTransport.ConnectAsync(Loopback, Material(key, port), TestContext.CancellationToken)
                .WaitAsync(Promptly, TestContext.CancellationToken));

        await killed;

        // The host sees the same classification a ceiling drop produces...
        Assert.IsTrue(
            failure.Error.Context?.ContainsKey(GuestSecureChannel.ClosedDuringHandshakeKey),
            "A killed agent resets mid-handshake just as a ceiling drop does.");

        // ...so the decision cannot rest on that alone. Nothing is listening now, which is what
        // sends this to repair while a live agent at its ceiling is reported busy instead.
        Assert.IsFalse(
            await GuestTcpTransport.IsListeningAsync(
                Loopback, port, TimeSpan.FromSeconds(5), TestContext.CancellationToken),
            "A dead agent must be recognised as gone so its layer is repaired.");
    }

    [TestMethod]
    public async Task WrongKey_IsAnAuthenticationFailureRatherThanAClosedHandshake()
    {
        // Authentication and tamper failures keep their own meaning. Classifying them as "the peer
        // closed" would report a key mismatch as a busy agent and never repair the stale material
        // that actually caused it.
        var (listener, port) = ListenOnLoopback();
        using var _ = listener;

        var guestKey = NewKey();
        var serving = Task.Run(async () =>
        {
            var client = await listener.AcceptTcpClientAsync(TestContext.CancellationToken);
            try
            {
                await GuestTcpTransport.EstablishAsync(
                    client, Material(guestKey, port), TestContext.CancellationToken);
            }
            catch (ExecutionTargetException)
            {
                // The guest rejects the host too; this test asserts on the host's view.
            }
        });

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => GuestTcpTransport.ConnectAsync(Loopback, Material(NewKey(), port), TestContext.CancellationToken)
                .WaitAsync(Promptly, TestContext.CancellationToken));

        await serving;

        Assert.IsFalse(
            failure.Error.Context?.ContainsKey(GuestSecureChannel.ClosedDuringHandshakeKey) ?? false,
            "A key mismatch must not be reported as the peer declining the connection.");
    }

    [TestMethod]
    public async Task IsListening_SeparatesALiveListenerFromADeadOne()
    {
        // The probe that tells a ceiling drop from a dead agent. Both reset a connection they
        // accepted, so what separates them is whether anything still answers afterwards.
        var (listener, port) = ListenOnLoopback();

        using (listener)
        {
            Assert.IsTrue(
                await GuestTcpTransport.IsListeningAsync(
                    Loopback, port, TimeSpan.FromSeconds(5), TestContext.CancellationToken));
        }

        Assert.IsFalse(
            await GuestTcpTransport.IsListeningAsync(
                Loopback, port, TimeSpan.FromSeconds(5), TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task TrackedCeilingBurst_RefusesWithoutDisturbingHealthyChannels()
    {
        // The end-to-end shape of the reviewed defect: a burst that drives the agent past its
        // tracked ceiling, over real sockets, while healthy channels are open.
        var key = NewKey();
        var (listener, port) = ListenOnLoopback();
        using var _ = listener;
        var material = Material(key, port);

        var limits = new GuestConnectionLimits(
            MaxConnections: 2,
            HandshakeTimeout: TimeSpan.FromSeconds(30),
            RefusedConnectionLifetime: TimeSpan.FromSeconds(10));

        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);

        var acceptor = new GuestConnectionAcceptor(
            new GuestTcpConnectionSource(listener, material),
            (transport, refusal) => new GuestCommandServer(
                transport,
                Epoch,
                new FakeGuestProcessHostFactory(),
                new StaticGuestSessionProbe(new GuestSessionInfo(1, "WinSta0", HasInputDesktop: true)),
                new GuestAgentIdentity("9.9.9", "abc123", "arm64", GuestProtocol.MinimumVersion, GuestProtocol.CurrentVersion))
            {
                AdmissionRefusal = refusal,
            },
            limits);

        var serving = acceptor.RunAsync(shutdown.Token);
        var healthy = new List<GuestCommandChannel>();

        try
        {
            // Two admitted channels, both actually working.
            for (var i = 0; i < limits.MaxConnections; i++)
            {
                var transport = await GuestTcpTransport
                    .ConnectAsync(Loopback, material, TestContext.CancellationToken)
                    .WaitAsync(Promptly, TestContext.CancellationToken);

                var channel = new GuestCommandChannel(transport, Epoch);
                channel.Start();
                healthy.Add(channel);

                Assert.AreEqual(
                    "arm64",
                    (await channel.GetCapabilitiesAsync(TestContext.CancellationToken)).Architecture);
            }

            // Everything past the bound is refused, and every refusal is coded rather than raw.
            for (var i = 0; i < limits.MaxTrackedConnections + 4; i++)
            {
                await AssertRefusedAsync(material, TestContext.CancellationToken);
            }

            // The healthy channels are untouched by the burst, which is the property that matters:
            // overload must not disturb what the agent is already serving.
            foreach (var channel in healthy)
            {
                Assert.AreEqual(
                    "arm64",
                    (await channel.GetCapabilitiesAsync(TestContext.CancellationToken)
                        .WaitAsync(Promptly, TestContext.CancellationToken)).Architecture);
            }

            // And the agent is still listening, so a host that asked "are you alive" gets yes —
            // which is what keeps a ceiling drop from being mistaken for a dead agent.
            Assert.IsTrue(
                await GuestTcpTransport.IsListeningAsync(
                    Loopback, port, TimeSpan.FromSeconds(5), TestContext.CancellationToken));
        }
        finally
        {
            foreach (var channel in healthy)
            {
                await channel.DisposeAsync();
            }

            await shutdown.CancelAsync();
            await serving;
        }
    }

    /// <summary>
    /// Asserts one connection is refused, whichever of the two refusal paths it takes.
    /// </summary>
    /// <remarks>
    /// A burst crosses both: below the tracked ceiling the agent authenticates and answers
    /// <c>sandbox_agent_busy</c>; above it the socket is dropped before the handshake and the host
    /// sees a closed handshake. Both are coded and both leave the agent serving; which one a given
    /// connection meets depends on how fast earlier refusals drain, so the test pins the guarantee
    /// rather than the timing.
    /// </remarks>
    private static async Task AssertRefusedAsync(
        GuestBootstrapMaterial material,
        CancellationToken cancellationToken)
    {
        IGuestTransport transport;

        try
        {
            transport = await GuestTcpTransport
                .ConnectAsync(Loopback, material, cancellationToken)
                .WaitAsync(Promptly, cancellationToken);
        }
        catch (ExecutionTargetException dropped)
        {
            Assert.IsTrue(
                dropped.Error.Context?.ContainsKey(GuestSecureChannel.ClosedDuringHandshakeKey),
                $"A dropped connection must be classified, not raw: {dropped.Error.Code} {dropped.Error.Message}");
            return;
        }

        await using (transport.ConfigureAwait(false))
        {
            var channel = new GuestCommandChannel(transport, Epoch);
            channel.Start();

            await using (channel.ConfigureAwait(false))
            {
                var refusal = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
                    () => channel.GetCapabilitiesAsync(cancellationToken).WaitAsync(Promptly, cancellationToken));

                Assert.AreEqual(
                    ExecutionTargetErrorCodes.AgentBusy,
                    refusal.Error.Code,
                    "An authenticated connection past the admission bound must stay a busy refusal.");
            }
        }
    }
}
