using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AccessLoop.Tests;

[TestClass]
public sealed class ProcessCommandTests
{
    [TestMethod]
    public async Task PrecancelledRequestDoesNotStartAProcess()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => ProcessCommand.RunAsync(
            "does-not-exist-accessloop.exe", [], Environment.CurrentDirectory,
            TimeSpan.FromSeconds(20), cancellation.Token));
    }

    [TestMethod]
    public async Task CapturesExitCodeAndBothStreams()
    {
        var result = await ProcessCommand.RunAsync("powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command",
                "[Console]::Out.Write('stdout'); [Console]::Error.Write('stderr'); exit 7"],
            Environment.CurrentDirectory, TimeSpan.FromSeconds(20), CancellationToken.None);
        Assert.AreEqual(7, result.ExitCode);
        Assert.AreEqual("stdout", result.Stdout);
        Assert.AreEqual("stderr", result.Stderr);
    }

    [TestMethod]
    public async Task TimeoutTerminatesOnlyItsChild()
    {
        await Assert.ThrowsExactlyAsync<TimeoutException>(() => ProcessCommand.RunAsync("powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"],
            Environment.CurrentDirectory, TimeSpan.FromMilliseconds(300), CancellationToken.None));
    }

    [TestMethod]
    public async Task CancellationIsNotReportedAsTimeout()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => ProcessCommand.RunAsync("powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"],
            Environment.CurrentDirectory, TimeSpan.FromSeconds(20), cancellation.Token));
    }

    [TestMethod]
    public async Task OutputLimitStopsAChildWithoutWaitingForItsDeadline()
    {
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ProcessCommand.RunAsync("powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command",
                "[Console]::Out.Write(('x' * 10000)); Start-Sleep -Seconds 30"],
            Environment.CurrentDirectory, TimeSpan.FromSeconds(10), CancellationToken.None, maximumOutputCharacters: 512));
    }

    [TestMethod]
    public async Task InvalidOutputLimitDoesNotLaunchAProcess()
    {
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => ProcessCommand.RunAsync(
            "does-not-exist-accessloop.exe", [], Environment.CurrentDirectory,
            TimeSpan.FromSeconds(20), CancellationToken.None, maximumOutputCharacters: 0));
    }
}
