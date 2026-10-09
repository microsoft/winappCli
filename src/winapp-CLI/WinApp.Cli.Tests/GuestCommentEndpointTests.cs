// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Services.DevTools.Comments;

namespace WinApp.Cli.Tests;

[TestClass]
public class GuestCommentEndpointTests
{
    private static long nextProcessStart = DateTime.UtcNow.Ticks;

    private static GuestProcessStart UniqueProcess() =>
        new(Environment.ProcessId, Interlocked.Increment(ref nextProcessStart));

    [TestMethod]
    public void OutgoingMessages_EnforceBoundWhileSerializing()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => GuestCommentFrames.Encode(
            new GuestCommentReply(Guid.NewGuid().ToString("N"), Error: new string('x', GuestCommentFrames.MaximumLength + 1)),
            GuestCommentsJsonContext.Default.GuestCommentReply));
    }

    [TestMethod]
    public async Task ActualOwnerAuthenticatedPipe_ReportsSavedOnlyAfterHostCommit()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pair = DuplexStreamPair.Create();
        using var host = pair.Client;
        using var guest = pair.Server;
        var process = UniqueProcess();
        var session = new GuestCommentSession(Guid.NewGuid().ToString("N"), "sandbox-bound", "epoch", process);
        var endpoint = new GuestCommentEndpoint(session);
        var serve = endpoint.RunAsync(guest, guest, _ => { }, timeout.Token, isAppAlive: () => true);
        var ready = GuestCommentFrames.ReadAsync(host, timeout.Token);
        await await Task.WhenAny(ready, serve);
        _ = await ready;
        var requestId = Guid.NewGuid().ToString("N");
        var saving = GuestCommentClient.ExchangeAsync(process, hello => new("apply",
            new(hello.BindingId, hello.TargetId, hello.Epoch, hello.Process, requestId, "note", "",
                new Comment { Id = "note", Text = "exact \ntext", Anchor = new() { SourceFile = "Main.xaml" } })), timeout.Token);
        var incoming = GuestCommentFrames.ReadAsync(host, timeout.Token);
        var first = await Task.WhenAny(incoming, serve, saving);
        await first;
        Assert.AreSame(incoming, first, "The endpoint and writer must remain pending until the host receives and acknowledges the request.");
        var bytes = await incoming;
        var request = JsonSerializer.Deserialize(bytes, GuestCommentsJsonContext.Default.GuestCommentEnvelope)!;
        Assert.AreEqual("exact \ntext", request.Request.Replacement!.Text);
        Assert.IsFalse(saving.IsCompleted, "Guest UI must not report saved on pipe launch or forwarding alone.");
        await GuestCommentFrames.WriteAsync(host, new GuestCommentReply(requestId,
            new GuestCommentCommit(new string('A', 64), request.Request.Replacement)),
            GuestCommentsJsonContext.Default.GuestCommentReply, timeout.Token);
        var reply = await saving;
        Assert.AreEqual(new string('A', 64), reply.Commit!.PersistedRevision);
        await timeout.CancelAsync();
        try
        {
            await serve;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            // Cancellation between requests exits the listener loop normally; a pending read throws.
        }
        Assert.IsTrue(serve.IsCompleted, "All endpoint tasks must finish before the test releases its streams.");
    }

    [TestMethod]
    public async Task HostDisconnectBeforeAcknowledgement_FailsGuestSaveWithoutFallback()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pair = DuplexStreamPair.Create();
        using var host = pair.Client;
        using var guest = pair.Server;
        var process = UniqueProcess();
        var session = new GuestCommentSession(Guid.NewGuid().ToString("N"), "sandbox-bound", "epoch", process);
        var serve = new GuestCommentEndpoint(session).RunAsync(guest, guest, _ => { }, timeout.Token, isAppAlive: () => true);
        _ = await GuestCommentFrames.ReadAsync(host, timeout.Token);
        var saving = GuestCommentClient.ExchangeAsync(process, hello => new("apply",
            new(hello.BindingId, hello.TargetId, hello.Epoch, hello.Process, Guid.NewGuid().ToString("N"), "note", "",
                new Comment { Id = "note", Text = "not acknowledged", Anchor = new() { SourceFile = "Main.xaml" } })), timeout.Token);
        _ = await GuestCommentFrames.ReadAsync(host, timeout.Token);
        host.Dispose();
        await Assert.ThrowsAsync<IOException>(() => serve);
        var error = await Assert.ThrowsAsync<IOException>(() => saving);
        StringAssert.Contains(error.Message, "Nothing was saved to a disposable guest store");
    }

    [TestMethod]
    public async Task SamePipeCannotBeClaimedByCompetingRelay()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = DuplexStreamPair.Create();
        var second = DuplexStreamPair.Create();
        using var host1 = first.Client;
        using var guest1 = first.Server;
        using var host2 = second.Client;
        using var guest2 = second.Server;
        var process = UniqueProcess();
        var session = new GuestCommentSession(Guid.NewGuid().ToString("N"), "sandbox-bound", "epoch", process);
        var serving = new GuestCommentEndpoint(session).RunAsync(guest1, guest1, _ => { }, timeout.Token, isAppAlive: () => true);
        var competing = new GuestCommentEndpoint(session with { BindingId = Guid.NewGuid().ToString("N") })
            .RunAsync(guest2, guest2, _ => { }, timeout.Token, isAppAlive: () => true);
        await Assert.ThrowsAsync<IOException>(() => competing);
        await timeout.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => serving);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(6)]
    [DataRow(7)]
    public async Task PeerRejectedBeforeHandshake_DoesNotForwardAHostRequest(int iteration)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pair = DuplexStreamPair.Create();
        using var host = pair.Client;
        using var guest = pair.Server;
        var process = UniqueProcess();
        var session = new GuestCommentSession(Guid.NewGuid().ToString("N"), "sandbox-bound", "epoch", process);
        using var forwarded = new MemoryStream();
        var serve = new GuestCommentEndpoint(session).RunAsync(guest, forwarded, _ => { }, timeout.Token,
            isAppAlive: () => true, verifyPeer: _ => throw new UnauthorizedAccessException("foreign owner/integrity"));
        var requests = 0;
        var saving = GuestCommentClient.ExchangeAsync(process, _ =>
        {
            Interlocked.Increment(ref requests);
            throw new AssertFailedException("Rejected peer received a handshake.");
        }, timeout.Token);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => serve);
        var failure = await Assert.ThrowsAsync<IOException>(() => saving);
        StringAssert.Contains(failure.Message, "Nothing was saved to a disposable guest store");
        Assert.AreEqual(0, requests, $"Rejection {iteration} must precede client request construction.");
        forwarded.Position = 0;
        var ready = JsonSerializer.Deserialize(await GuestCommentFrames.ReadAsync(forwarded, timeout.Token),
            GuestCommentsJsonContext.Default.GuestCommentEnvelope);
        Assert.AreEqual("ready", ready!.Kind);
        Assert.AreEqual(forwarded.Length, forwarded.Position, "No client request bytes were forwarded to the host.");
    }

    [TestMethod]
    public async Task HostConflict_IsReportedInsteadOfSaveSuccess()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pair = DuplexStreamPair.Create();
        using var host = pair.Client;
        using var guest = pair.Server;
        var process = UniqueProcess();
        var session = new GuestCommentSession(Guid.NewGuid().ToString("N"), "sandbox-bound", "epoch", process);
        var serve = new GuestCommentEndpoint(session).RunAsync(guest, guest, _ => { }, timeout.Token, isAppAlive: () => true);
        _ = await GuestCommentFrames.ReadAsync(host, timeout.Token);
        var requestId = Guid.NewGuid().ToString("N");
        var saving = GuestCommentClient.ExchangeAsync(process, hello => new("apply",
            new(hello.BindingId, hello.TargetId, hello.Epoch, hello.Process, requestId, "note", new string('A', 64),
                new Comment { Id = "note", Text = "guest edit", Anchor = new() { SourceFile = "Main.xaml" } })), timeout.Token);
        _ = await GuestCommentFrames.ReadAsync(host, timeout.Token);
        await GuestCommentFrames.WriteAsync(host, new GuestCommentReply(requestId, Error: "Comment changed on the host. Refresh."),
            GuestCommentsJsonContext.Default.GuestCommentReply, timeout.Token);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => saving);
        StringAssert.Contains(error.Message, "changed on the host");
        await timeout.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => serve);
    }
}
