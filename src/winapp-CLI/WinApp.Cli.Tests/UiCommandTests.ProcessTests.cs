// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text;

namespace WinApp.Cli.Tests;

public partial class UiCommandTests
{
    [TestMethod]
    public async Task ProcessHelper_TimeoutRetainsOutputAndObservesOwnedChildExit()
    {
        var elapsed = Stopwatch.StartNew();
        var failure = await Assert.ThrowsExactlyAsync<AssertFailedException>(() => RunProcessAsync(
            Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            ["/d", "/c", "echo timeout-stdout-ready & echo timeout-stderr-ready 1>&2 & ping -n 30 127.0.0.1 >nul"],
            TimeSpan.FromSeconds(2)));

        Assert.Contains("stdout (captured, may be partial):\ntimeout-stdout-ready", failure.Message);
        Assert.Contains("stderr (captured, may be partial):\ntimeout-stderr-ready", failure.Message);
        Assert.Contains("deadlineMs=2000", failure.Message);
        Assert.Contains("elapsedMs=", failure.Message);
        Assert.Contains("pid=", failure.Message);
        Assert.Contains("exitedAtDeadline=False", failure.Message);
        Assert.Contains("exitedAfterCleanup=True", failure.Message);
        Assert.Contains("outputDrain=stopped", failure.Message);
        Assert.IsTrue(elapsed.Elapsed >= TimeSpan.FromSeconds(2), "The requested deadline must not be shortened.");
        Assert.IsTrue(elapsed.Elapsed < TimeSpan.FromSeconds(15), "Cleanup and output draining must remain bounded.");
    }

    [TestMethod]
    public async Task ProcessHelper_NormalExitPreservesOutputAndExitCode()
    {
        var result = await RunProcessAsync(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            ["/d", "/c", "echo normal-stdout & echo normal-stderr 1>&2 & exit /b 3"], TimeSpan.FromSeconds(20));

        Assert.AreEqual(3, result.ExitCode);
        Assert.Contains("normal-stdout", result.Stdout);
        Assert.Contains("normal-stderr", result.Stderr);
    }

    [TestMethod]
    public void ProcessHelper_DiagnosticsTruncateWithoutTruncatingSuccessfulOutput()
    {
        var output = new StringBuilder(new string('x', 12000)).Append("latest-marker");
        var diagnostic = ProcessOutputSnapshot(output, 8192);

        Assert.StartsWith("[truncated ", diagnostic);
        Assert.EndsWith("latest-marker", diagnostic);
        Assert.IsTrue(diagnostic.Length < 8300);
        Assert.AreEqual(output.ToString(), ProcessOutputSnapshot(output));
    }
}
