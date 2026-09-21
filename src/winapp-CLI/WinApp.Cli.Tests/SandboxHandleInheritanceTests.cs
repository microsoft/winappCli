// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.InteropServices;
using WinApp.Cli.ExecutionTargets.WindowsSandbox;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// A child that outlives winapp must not hold the caller's captured output open (SBX-001).
/// </summary>
/// <remarks>
/// <para>
/// When a script or SDK captures winapp's output, winapp's stdout and stderr are pipe handles owned
/// by that caller. The caller reads to end of stream, and end of stream arrives only when the last
/// handle to the write end closes. A Sandbox run deliberately leaves processes running afterwards —
/// the client window, and the <c>wsb exec</c> hosting the persistent guest agent — so if either
/// inherits those handles the caller blocks long after the command finished. Observed as: winapp
/// exits 0 in ~20s, the caller's <c>ReadToEndAsync</c> still pending 30s later, and killing
/// <c>wsb.exe</c> releasing it.
/// </para>
/// <para>
/// The non-obvious part, and the reason a plausible fix is wrong: redirecting the child's own
/// streams does <b>not</b> prevent this. .NET calls <c>CreateProcess</c> with
/// <c>bInheritHandles: true</c> whenever it redirects anything, so every inheritable handle is
/// duplicated into the child no matter what <c>STARTF_USESTDHANDLES</c> points at. Measured on this
/// machine: an unredirected child leaks, a fully redirected child leaks identically, and only
/// clearing <c>HANDLE_FLAG_INHERIT</c> around the launch closes the caller's stream promptly.
/// </para>
/// </remarks>
[TestClass]
[DoNotParallelize]
public partial class SandboxHandleInheritanceTests
{
    private const int StdOutputHandle = -11;
    private const uint HandleFlagInherit = 0x00000001;

    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// The guest-agent launch — the one child that runs for the life of the Sandbox — is marked as
    /// outliving the caller.
    /// </summary>
    [TestMethod]
    public async Task AgentLaunch_IsMarkedAsOutlivingTheCaller()
    {
        var runner = new CapturingProcessRunner();
        var cli = new WindowsSandboxCli(runner);

        if (!cli.IsAvailable)
        {
            Assert.Inconclusive("wsb.exe is not present, so the launch path cannot be exercised.");
            return;
        }

        await cli.LaunchAgentAsync("sandbox-1", @"C:\WinAppBootstrap\winapp.exe guest-agent", TestContext.CancellationToken);

        var request = runner.Requests.Single(r => r.Arguments.Contains("exec"));

        Assert.IsTrue(
            request.OutlivesCaller,
            "The persistent guest-agent launch must not inherit the caller's standard handles.");
    }

    /// <summary>Short-lived Sandbox commands keep ordinary inheritance.</summary>
    /// <remarks>
    /// The suppression is deliberately scoped to launches that outlive winapp. Applying it
    /// everywhere would be a broader change than the defect needs, and standard handles must stay
    /// inheritable for pass-through children such as <c>winapp tool</c> and the MSBuild steps that
    /// write straight to winapp's console.
    /// </remarks>
    [TestMethod]
    public async Task ShortLivedCommands_KeepOrdinaryInheritance()
    {
        var runner = new CapturingProcessRunner();
        var cli = new WindowsSandboxCli(runner);

        if (!cli.IsAvailable)
        {
            Assert.Inconclusive("wsb.exe is not present, so the launch path cannot be exercised.");
            return;
        }

        await cli.ListAsync(TestContext.CancellationToken);

        Assert.IsFalse(
            runner.Requests.Single().OutlivesCaller,
            "A command winapp waits for cannot outlive it, so it needs no suppression.");
    }

    /// <summary>
    /// The suppression scope actually clears, and then restores, inheritance on a standard handle.
    /// </summary>
    /// <remarks>
    /// Asserted against the real Win32 handle state rather than a flag on an object, because the
    /// whole defect is that the flag on the object was never the thing that mattered. Restoration is
    /// as load-bearing as suppression: leaving standard handles non-inheritable would break every
    /// later pass-through child.
    /// </remarks>
    [TestMethod]
    public void SuppressionScope_ClearsInheritanceAndRestoresIt()
    {
        var handle = GetStdHandle(StdOutputHandle);

        if (handle == 0 || handle == -1 || !GetHandleInformation(handle, out var before))
        {
            Assert.Inconclusive("This test host has no inspectable standard output handle.");
            return;
        }

        if ((before & HandleFlagInherit) == 0)
        {
            Assert.Inconclusive("Standard output is already non-inheritable in this test host.");
            return;
        }

        using (StandardHandleInheritance.Suppress())
        {
            Assert.IsTrue(GetHandleInformation(handle, out var during));
            Assert.AreEqual(
                0u,
                during & HandleFlagInherit,
                "A child started inside the scope must not be able to inherit the caller's pipe.");
        }

        Assert.IsTrue(GetHandleInformation(handle, out var after));
        Assert.AreEqual(
            HandleFlagInherit,
            after & HandleFlagInherit,
            "Inheritance must be restored, or later pass-through children lose their output.");
    }

    /// <summary>Nesting and double disposal leave the handle in its original state.</summary>
    [TestMethod]
    public void SuppressionScope_IsSafeToDisposeTwice()
    {
        var handle = GetStdHandle(StdOutputHandle);

        if (handle == 0 || handle == -1 ||
            !GetHandleInformation(handle, out var before) ||
            (before & HandleFlagInherit) == 0)
        {
            Assert.Inconclusive("This test host has no inheritable standard output handle.");
            return;
        }

        var scope = StandardHandleInheritance.Suppress();
        scope.Dispose();
        scope.Dispose();

        Assert.IsTrue(GetHandleInformation(handle, out var after));
        Assert.AreEqual(HandleFlagInherit, after & HandleFlagInherit);
    }

    /// <summary>Records the requests production built, without starting anything.</summary>
    private sealed class CapturingProcessRunner : IProcessRunner
    {
        public List<ProcessRunRequest> Requests { get; } = [];

        public Task<ProcessRunResult> RunAsync(
            ProcessRunRequest request,
            Action<string>? onOutputLine = null,
            Action<string>? onErrorLine = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new ProcessRunResult(0, "{}", string.Empty));
        }
    }

    // Deliberately its own P/Invoke declarations rather than a call through
    // StandardHandleInheritance (the production type under test, just above): these two tests exist
    // to verify what that type's Suppress() scope actually does to the real Win32 handle state, so
    // reading that state back through the same type being verified would make the assertions
    // tautological -- a regression in StandardHandleInheritance's own handle-flag logic could no
    // longer be caught. Source-generated (LibraryImport) rather than classic DllImport, matching
    // StandardHandleInheritance's own declarations, with the same SetLastError and BOOL marshalling.
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GetStdHandle(int nStdHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetHandleInformation(nint hObject, out uint lpdwFlags);
}
