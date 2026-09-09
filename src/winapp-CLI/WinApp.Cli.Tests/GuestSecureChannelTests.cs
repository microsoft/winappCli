// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for <see cref="GuestSecureChannel"/>, the authenticated transport between host winapp and
/// the guest agent. Every test runs over an in-memory duplex pair, so the whole handshake and
/// framing contract is verified without Windows Sandbox, a network stack, or a real guest.
/// </summary>
[TestClass]
public class GuestSecureChannelTests
{
    private const string TargetId = "sandbox-default-6b0d287c0c51bc40";
    private const string Epoch = "instance-1:nonce-1";

    private static byte[] NewPreSharedKey() => RandomNumberGenerator.GetBytes(GuestProtocol.PreSharedKeySize);

    /// <summary>
    /// Establishes both ends concurrently, which is required because the handshake is a real
    /// exchange: each side blocks until the other has written.
    /// </summary>
    private static async Task<(GuestSecureChannel Host, GuestSecureChannel Guest)> EstablishAsync(
        byte[] hostKey,
        byte[] guestKey,
        string hostEpoch = Epoch,
        string guestEpoch = Epoch,
        string hostTargetId = TargetId,
        string guestTargetId = TargetId)
    {
        var (clientStream, serverStream) = DuplexStreamPair.Create();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var hostTask = GuestSecureChannel.EstablishAsync(
            clientStream, GuestRole.Host, hostKey, hostTargetId, hostEpoch, timeout.Token);
        var guestTask = GuestSecureChannel.EstablishAsync(
            serverStream, GuestRole.Guest, guestKey, guestTargetId, guestEpoch, timeout.Token);

        await Task.WhenAll(hostTask, guestTask);
        return (hostTask.Result, guestTask.Result);
    }

    /// <summary>
    /// Runs both ends and returns the failure from whichever side rejected first, tolerating the
    /// other side failing only because its peer went away.
    /// </summary>
    private static async Task<ExecutionTargetException> EstablishExpectingFailureAsync(
        byte[] hostKey,
        byte[] guestKey,
        string hostEpoch = Epoch,
        string guestEpoch = Epoch,
        string hostTargetId = TargetId,
        string guestTargetId = TargetId)
    {
        var (clientStream, serverStream) = DuplexStreamPair.Create();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var hostTask = GuestSecureChannel.EstablishAsync(
            clientStream, GuestRole.Host, hostKey, hostTargetId, hostEpoch, timeout.Token);
        var guestTask = GuestSecureChannel.EstablishAsync(
            serverStream, GuestRole.Guest, guestKey, guestTargetId, guestEpoch, timeout.Token);

        var failures = new List<ExecutionTargetException>();
        foreach (var task in new[] { hostTask, guestTask })
        {
            try
            {
                var channel = await task;
                await channel.DisposeAsync();
            }
            catch (ExecutionTargetException ex)
            {
                failures.Add(ex);
            }
        }

        Assert.IsTrue(failures.Count > 0, "The handshake was expected to fail on at least one side.");
        return failures[0];
    }

    [TestMethod]
    public async Task Establish_WithSharedKey_SucceedsAndAgreesOnVersion()
    {
        var psk = NewPreSharedKey();

        var (host, guest) = await EstablishAsync(psk, psk);
        await using (host)
        await using (guest)
        {
            Assert.IsTrue(host.IsConnected);
            Assert.IsTrue(guest.IsConnected);
            Assert.AreEqual(GuestProtocol.CurrentVersion, host.NegotiatedVersion);
            Assert.AreEqual(host.NegotiatedVersion, guest.NegotiatedVersion);
        }
    }

    [TestMethod]
    public async Task Frames_RoundTripInBothDirections()
    {
        var psk = NewPreSharedKey();
        var (host, guest) = await EstablishAsync(psk, psk);

        await using (host)
        await using (guest)
        {
            var request = Encoding.UTF8.GetBytes("run --on sandbox");
            await host.SendFrameAsync(request, TestContext.CancellationTokenSource.Token);
            var received = await guest.ReceiveFrameAsync(TestContext.CancellationTokenSource.Token);
            CollectionAssert.AreEqual(request, received!.Value.ToArray());

            var response = Encoding.UTF8.GetBytes("started");
            await guest.SendFrameAsync(response, TestContext.CancellationTokenSource.Token);
            var back = await host.ReceiveFrameAsync(TestContext.CancellationTokenSource.Token);
            CollectionAssert.AreEqual(response, back!.Value.ToArray());
        }
    }

    [TestMethod]
    public async Task Frames_PreserveOrderAndUnicodePayloads()
    {
        var psk = NewPreSharedKey();
        var (host, guest) = await EstablishAsync(psk, psk);

        await using (host)
        await using (guest)
        {
            var payloads = new[]
            {
                "first",
                "second \u2014 em dash",
                "third \U0001F600 emoji",
                new string('x', 64 * 1024),
                string.Empty,
            };

            foreach (var payload in payloads)
            {
                await host.SendFrameAsync(Encoding.UTF8.GetBytes(payload), TestContext.CancellationTokenSource.Token);
            }

            foreach (var expected in payloads)
            {
                var frame = await guest.ReceiveFrameAsync(TestContext.CancellationTokenSource.Token);
                Assert.AreEqual(expected, Encoding.UTF8.GetString(frame!.Value.Span));
            }
        }
    }

    [TestMethod]
    public async Task Establish_WithDifferentPreSharedKeys_FailsAuthentication()
    {
        // An unrelated host or network caller does not hold the per-boot secret, so it cannot drive
        // the agent even though it can reach the port.
        var failure = await EstablishExpectingFailureAsync(NewPreSharedKey(), NewPreSharedKey());

        Assert.AreEqual(ExecutionTargetErrorCodes.TransportFailed, failure.Error.Code);
    }

    [TestMethod]
    public async Task Establish_WithMismatchedEpoch_IsRejectedAsStaleTarget()
    {
        var psk = NewPreSharedKey();

        var failure = await EstablishExpectingFailureAsync(
            psk, psk, hostEpoch: "instance-1:nonce-1", guestEpoch: "instance-1:nonce-2");

        // Talking to a recreated guest with a stale expectation must be refused rather than allowed
        // to act on assumptions from a previous generation.
        Assert.AreEqual(ExecutionTargetErrorCodes.TargetStale, failure.Error.Code);
    }

    [TestMethod]
    public async Task Establish_WithMismatchedTarget_IsRejected()
    {
        var psk = NewPreSharedKey();

        var failure = await EstablishExpectingFailureAsync(
            psk, psk, hostTargetId: "sandbox-default-6b0d287c0c51bc40", guestTargetId: "hyperv:other");

        Assert.AreEqual(ExecutionTargetErrorCodes.TransportFailed, failure.Error.Code);
    }

    [TestMethod]
    public async Task Establish_WithNonAgentPeer_ReportsTransportFailure()
    {
        var (clientStream, serverStream) = DuplexStreamPair.Create();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // A peer that is not a winapp agent at all: correct byte count, wrong magic.
        var junk = new byte[GuestProtocol.HelloSize];
        Array.Fill(junk, (byte)'Z');
        await serverStream.WriteAsync(junk, timeout.Token);

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => GuestSecureChannel.EstablishAsync(
                clientStream, GuestRole.Host, NewPreSharedKey(), TargetId, Epoch, timeout.Token));

        Assert.AreEqual(ExecutionTargetErrorCodes.TransportFailed, failure.Error.Code);
        StringAssert.Contains(failure.Message, "not a winapp guest agent", StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task Establish_WhenPeerClosesImmediately_ReportsTransportFailure()
    {
        var (clientStream, serverStream) = DuplexStreamPair.Create();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await serverStream.DisposeAsync();

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => GuestSecureChannel.EstablishAsync(
                clientStream, GuestRole.Host, NewPreSharedKey(), TargetId, Epoch, timeout.Token));

        Assert.AreEqual(ExecutionTargetErrorCodes.TransportFailed, failure.Error.Code);
    }

    [TestMethod]
    public async Task Receive_AfterPeerClosesCleanly_ReportsEndOfStreamNotFailure()
    {
        var psk = NewPreSharedKey();
        var (host, guest) = await EstablishAsync(psk, psk);

        await using (host)
        {
            await guest.DisposeAsync();

            // "The guest finished and went away" must stay distinguishable from "the channel broke",
            // because only the latter is an infrastructure failure.
            var frame = await host.ReceiveFrameAsync(TestContext.CancellationTokenSource.Token);

            Assert.IsNull(frame);
            Assert.IsFalse(host.IsConnected);
        }
    }

    [TestMethod]
    public async Task Receive_ForgedFrameFromAnUnauthenticatedPeer_IsRejected()
    {
        var (clientStream, serverStream) = DuplexStreamPair.Create();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var psk = NewPreSharedKey();

        var hostTask = GuestSecureChannel.EstablishAsync(
            clientStream, GuestRole.Host, psk, TargetId, Epoch, timeout.Token);
        var guestTask = GuestSecureChannel.EstablishAsync(
            serverStream, GuestRole.Guest, psk, TargetId, Epoch, timeout.Token);
        await Task.WhenAll(hostTask, guestTask);

        await using var host = hostTask.Result;
        await using var guest = guestTask.Result;

        // Inject bytes that were not produced by the negotiated keys, as a hostile peer on the
        // guest port would.
        using var forged = new GuestFrameCodec(new byte[GuestFrameCodec.KeySize], new byte[GuestFrameCodec.NoncePrefixSize]);
        var frame = new byte[GuestFrameCodec.GetEncodedSize(4)];
        forged.Encode([1, 2, 3, 4], 1, frame);
        await serverStream.WriteAsync(frame, timeout.Token);

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            async () => await host.ReceiveFrameAsync(timeout.Token));

        Assert.AreEqual(ExecutionTargetErrorCodes.TransportFailed, failure.Error.Code);
    }

    [TestMethod]
    public async Task Receive_OversizedLengthPrefix_IsRejectedWithoutAllocating()
    {
        var (clientStream, serverStream) = DuplexStreamPair.Create();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var psk = NewPreSharedKey();

        var hostTask = GuestSecureChannel.EstablishAsync(
            clientStream, GuestRole.Host, psk, TargetId, Epoch, timeout.Token);
        var guestTask = GuestSecureChannel.EstablishAsync(
            serverStream, GuestRole.Guest, psk, TargetId, Epoch, timeout.Token);
        await Task.WhenAll(hostTask, guestTask);

        await using var host = hostTask.Result;
        await using var guest = guestTask.Result;

        // A hostile peer declaring a 4 GiB frame must be rejected on the header alone, before the
        // reader ever tries to buffer the body.
        await serverStream.WriteAsync(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, timeout.Token);

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            async () => await host.ReceiveFrameAsync(timeout.Token));

        Assert.AreEqual(ExecutionTargetErrorCodes.TransportFailed, failure.Error.Code);
    }

    /// <summary>MSTest injects this; used for per-test cancellation.</summary>
    public TestContext TestContext { get; set; } = null!;
}
