// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using WinApp.Cli.Commands;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Models;
using WinApp.Cli.Services;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Tests;

[TestClass]
public class DevToolsWatchSessionTests
{
    private static readonly int[] ExpectedPids = [10, 11];

    [TestMethod]
    public async Task AttachesEachNewAppProcessOnce_AndReportsRestarts()
    {
        var apps = new List<int> { 10 };
        var connected = new List<int>();
        var reports = new List<(int Pid, bool Restart)>();
        var session = new DevToolsWatchSession(() => apps, (pid, _) =>
        {
            connected.Add(pid);
            return Task.FromResult(DevToolsConnection.Ok(5));
        }, () => false, _ => true)
        {
            Attached = (pid, _, restart) => reports.Add((pid, restart)),
        };

        await session.PollOnceAsync(CancellationToken.None);
        await session.PollOnceAsync(CancellationToken.None);
        apps = [11];
        await session.PollOnceAsync(CancellationToken.None);

        CollectionAssert.AreEqual(ExpectedPids, connected);
        CollectionAssert.AreEqual(new[] { (10, false), (11, true) }, reports);
        Assert.AreEqual(2, session.AttachedCount);
    }

    [TestMethod]
    public async Task RetriesAFailedAttach_ThenGivesUpAndSaysSo()
    {
        var attempts = 0;
        var reports = 0;
        var session = new DevToolsWatchSession(() => [7], (_, _) =>
        {
            attempts++;
            return Task.FromResult(DevToolsConnection.Fail("no tree"));
        }, () => false, _ => true)
        {
            Attached = (_, connection, _) => { reports++; Assert.IsFalse(connection.Connected); },
        };

        for (var i = 0; i < 6; i++)
        {
            await session.PollOnceAsync(CancellationToken.None);
        }

        Assert.AreEqual(DevToolsWatchSession.MaxAttempts, attempts);
        Assert.AreEqual(1, reports, "one warning, not one per poll");
    }

    [TestMethod]
    public async Task SkipsAnAppThatAlreadyExited()
    {
        var attempts = 0;
        var session = new DevToolsWatchSession(() => [3], (_, _) => { attempts++; return Task.FromResult(DevToolsConnection.Ok(1)); },
            () => false, _ => false);

        await session.PollOnceAsync(CancellationToken.None);

        Assert.AreEqual(0, attempts);
    }

    [TestMethod]
    public async Task Run_StopsWhenDotnetWatchExits()
    {
        var polls = 0;
        var session = new DevToolsWatchSession(() => { polls++; return []; }, (_, _) => Task.FromResult(DevToolsConnection.Ok(1)),
            () => polls >= 3, _ => true)
        {
            Delay = (_, _) => Task.CompletedTask,
        };

        await session.RunAsync(CancellationToken.None);

        Assert.AreEqual(3, polls);
    }

    [TestMethod]
    public void WatchArguments_MirrorTheBuildPass_AndPassAppArgumentsAfterTheSeparator()
    {
        var csproj = new FileInfo(@"C:\src\My App\App.csproj");
        var options = new ProjectRunOptions("Release", "arm64", "net10.0-windows10.0.26100.0", false, false,
            ["DefineConstants=DEMO", "Configuration=Debug"], Platform: "ARM64");

        var arguments = ProjectRunService.BuildWatchArguments(csproj, options, "--preview Counter");

        Assert.AreEqual("watch --project \"C:\\src\\My App\\App.csproj\" --non-interactive --no-launch-profile -c Release " +
            "-r win-arm64 -f net10.0-windows10.0.26100.0 --property:DefineConstants=DEMO --property:Platform=ARM64 -- --preview Counter", arguments);
        StringAssert.EndsWith(ProjectRunService.BuildWatchArguments(csproj, options with { OmitRuntimeIdentifier = true }, null),
            "--property:Platform=ARM64");
        Assert.IsFalse(ProjectRunService.BuildWatchArguments(csproj, options with { OmitRuntimeIdentifier = true }, null).Contains(" -r ", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(false, false, false, false, false, false, true, "can't be combined with --devtools off")]
    [DataRow(true, true, true, false, false, false, true, "--detach, --json")]
    [DataRow(true, false, false, true, true, true, false, "--debug-output, --no-build, --aot, --on")]
    public void WatchConflict_NamesEveryConflictingOption(bool devTools, bool detach, bool json, bool debugOutput, bool noBuild,
        bool aot, bool local, string expected) =>
        StringAssert.Contains(RunCommand.WatchConflict(devTools, detach, json, debugOutput, noBuild, aot, local), expected);

    [TestMethod]
    public void WatchConflict_AllowsAPlainDevToolsRun() =>
        Assert.IsNull(RunCommand.WatchConflict(true, false, false, false, false, false, true));

    [TestMethod]
    public void Descendants_FindsAChildAndItsImageName()
    {
        using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 6 127.0.0.1 >nul")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        try
        {
            IReadOnlyList<(int ProcessId, string ImageName)> descendants = [];
            for (var i = 0; i < 50 && !descendants.Any(d => string.Equals(d.ImageName, "PING.EXE", StringComparison.OrdinalIgnoreCase)); i++)
            {
                Thread.Sleep(50);
                descendants = ParentProcessId.Descendants(Environment.ProcessId);
            }
            Assert.IsTrue(descendants.Any(d => d.ProcessId == child.Id && string.Equals(d.ImageName, "cmd.exe", StringComparison.OrdinalIgnoreCase)),
                string.Join(", ", descendants));
            Assert.IsTrue(descendants.Any(d => string.Equals(d.ImageName, "PING.EXE", StringComparison.OrdinalIgnoreCase)), "grandchildren too");
        }
        finally
        {
            child.Kill(entireProcessTree: true);
        }
    }
}
