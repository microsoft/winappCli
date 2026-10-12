// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

[TestClass]
public class FrameGrabberReadinessTests
{
    [TestMethod]
    public async Task GetFrame_LegacyReadinessThenMissingFrame_PollsUntilReady()
    {
        var reads = 0;
        byte[] pixels = [0, 255, 0, 255];
        using IFrameGrabber grabber = new LegacyGrabber(
            (_, _) => Task.FromResult(true),
            () => ++reads < 3 ? null : (pixels, 1, 1, 7));

        var frame = await grabber.GetFrameAsync(TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.IsNotNull(frame);
        Assert.AreSame(pixels, frame.Value.Pixels);
        Assert.AreEqual(7L, frame.Value.Version);
        Assert.AreEqual(3, reads);
    }

    [TestMethod]
    public async Task GetFrame_LegacyFailure_DoesNotReadPixels()
    {
        using IFrameGrabber grabber = new LegacyGrabber(
            (_, _) => Task.FromResult(false),
            () => throw new AssertFailedException("A failed legacy wait must remain a failure."));
        Assert.IsNull(await grabber.GetFrameAsync(TimeSpan.FromSeconds(1), CancellationToken.None));
    }

    [TestMethod]
    public async Task GetFrame_LegacyError_IsPreserved()
    {
        var failure = new InvalidOperationException("capture startup failed");
        using IFrameGrabber grabber = new LegacyGrabber(
            (_, _) => Task.FromException<bool>(failure), () => null);
        Assert.AreSame(failure, await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            grabber.GetFrameAsync(TimeSpan.FromSeconds(1), CancellationToken.None)));
    }

    [TestMethod]
    public async Task GetFrame_LegacyWaitUsesDeadline_NoNewPollingBudget()
    {
        var reads = 0;
        using IFrameGrabber grabber = new LegacyGrabber(
            async (timeout, ct) =>
            {
                await Task.Delay(timeout, ct);
                return true;
            },
            () => { reads++; return null; });
        var timer = Stopwatch.StartNew();

        Assert.IsNull(await grabber.GetFrameAsync(TimeSpan.FromMilliseconds(60), CancellationToken.None));

        Assert.AreEqual(1, reads, "The legacy wait already consumed the polling deadline.");
        Assert.IsLessThan(1000L, timer.ElapsedMilliseconds);
    }

    [TestMethod]
    public async Task GetFrame_NotReady_StopsAtDeadline()
    {
        using IFrameGrabber grabber = new LegacyGrabber((_, _) => Task.FromResult(true), () => null);
        var timer = Stopwatch.StartNew();
        Assert.IsNull(await grabber.GetFrameAsync(TimeSpan.FromMilliseconds(60), CancellationToken.None));
        Assert.IsGreaterThanOrEqualTo(50L, timer.ElapsedMilliseconds);
        Assert.IsLessThan(1000L, timer.ElapsedMilliseconds);
    }

    [TestMethod]
    public async Task GetFrame_CancellationDuringPolling_IsPreserved()
    {
        using var cancellation = new CancellationTokenSource();
        using IFrameGrabber grabber = new LegacyGrabber((_, _) => Task.FromResult(true), () =>
        {
            cancellation.Cancel();
            return null;
        });
        var error = await Assert.ThrowsExactlyAsync<TaskCanceledException>(() =>
            grabber.GetFrameAsync(TimeSpan.FromSeconds(1), cancellation.Token));
        Assert.AreEqual(cancellation.Token, error.CancellationToken);
    }

    [TestMethod]
    public async Task GetFrame_CombinedFrameRemainsUsableWhenNewMenuInvalidatesNextRead()
    {
        var discoveries = 0;
        byte[] pixels = [0, 255, 0, 255];
        using IFrameGrabber grabber = new WindowCaptureSession(
            new LegacyGrabber((_, _) => Task.FromResult(true), () => (pixels, 1, 1, 1)),
            () => new(0, 0, 1, 1),
            () => ++discoveries <= 2 ? [] : [new(42, new(0, 0, 1, 1))],
            _ => new LegacyGrabber((_, _) => Task.FromResult(true), () => null));

        var frame = await grabber.GetFrameAsync(TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.IsNotNull(frame);
        Assert.IsNull(grabber.TryGetLatest());
        Assert.AreSame(pixels, frame.Value.Pixels);
    }

    private sealed class LegacyGrabber(
        Func<TimeSpan, CancellationToken, Task<bool>> wait,
        Func<(byte[] Pixels, int Width, int Height, long Version)?> read) : IFrameGrabber
    {
        public bool IsClosed => false;
        public Task<bool> WaitForFirstFrameAsync(TimeSpan timeout, CancellationToken ct) => wait(timeout, ct);
        public (byte[] Pixels, int Width, int Height, long Version)? TryGetLatest() => read();
        public void Dispose() { }
    }
}
