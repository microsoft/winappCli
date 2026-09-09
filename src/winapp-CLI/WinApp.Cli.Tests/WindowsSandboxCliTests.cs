// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.WindowsSandbox;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>Records what was launched so the resolved executable path can be asserted.</summary>
internal sealed class RecordingProcessRunner : IProcessRunner
{
    public List<ProcessRunRequest> Requests { get; } = [];

    public ProcessRunResult Result { get; set; } = new(0, "{}", string.Empty);

    public Task<ProcessRunResult> RunAsync(
        ProcessRunRequest request,
        Action<string>? onOutputLine = null,
        Action<string>? onErrorLine = null,
        CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        return Task.FromResult(Result);
    }
}

/// <summary>
/// Regression tests for how winapp invokes <c>wsb.exe</c>.
/// </summary>
/// <remarks>
/// Both behaviours here were found by independent review of PR #779 and reproduced empirically.
/// </remarks>
[TestClass]
[DoNotParallelize]
public class WindowsSandboxCliTests
{
    private static readonly string[] ConnectArguments = ["connect", "--id", "sandbox-1", "--raw"];

    private RecordingProcessRunner _runner = null!;
    private WindowsSandboxCli _cli = null!;

    [TestInitialize]
    public void Setup()
    {
        _runner = new RecordingProcessRunner();
        _cli = new WindowsSandboxCli(_runner);
    }

    [TestMethod]
    public void ResolveTrustedAlias_ReturnsAFullyQualifiedPath()
    {
        var resolved = WindowsSandboxHostProbe.ResolveTrustedAlias();

        if (resolved is null)
        {
            Assert.Inconclusive("wsb.exe is not installed on this machine.");
            return;
        }

        Assert.IsTrue(Path.IsPathFullyQualified(resolved), "wsb.exe must be resolved to an absolute path.");
        Assert.IsTrue(File.Exists(resolved));
    }

    /// <summary>
    /// A <c>wsb.exe</c> on PATH is never resolved, never bound, and never executed.
    /// </summary>
    /// <remarks>
    /// Regression for a real hole. Resolution used to try PATH first and take the first absolute
    /// entry containing a file named <c>wsb.exe</c>. PATH is an ordered list winapp does not
    /// control, and its entries are routinely directories written by installers, build agents, or
    /// other principals — so a planted binary there became the Sandbox control plane. Readiness
    /// probing then made it worse by <em>running</em> that binary, before the user's project was
    /// even built.
    /// <para>
    /// The decoy here is a real executable that would succeed if it were ever launched, so the
    /// assertion is not merely "the path was not returned" but "nothing ran it".
    /// </para>
    /// </remarks>
    [TestMethod]
    public void ResolveTrustedAlias_NeverConsultsPath()
    {
        var decoyDirectory = new DirectoryInfo(TestPaths.TempRoot("wsb-decoy"));
        decoyDirectory.Create();

        var decoy = TestPaths.Under(decoyDirectory.FullName, WindowsSandboxCli.ExecutableName);
        var originalPath = Environment.GetEnvironmentVariable("PATH");

        try
        {
            File.WriteAllText(decoy, "decoy");

            // The decoy directory is first, and absolute, so the old resolver would have taken it.
            Environment.SetEnvironmentVariable("PATH", $"{decoyDirectory.FullName};{originalPath}");

            var resolved = WindowsSandboxHostProbe.ResolveTrustedAlias();

            Assert.AreNotEqual(
                decoy,
                resolved,
                StringComparer.OrdinalIgnoreCase,
                "A wsb.exe on PATH must never become the Sandbox control plane.");

            if (resolved is not null)
            {
                Assert.IsFalse(
                    resolved.StartsWith(decoyDirectory.FullName, StringComparison.OrdinalIgnoreCase),
                    "Resolution must not come from any PATH entry.");
                StringAssert.Contains(
                    resolved,
                    @"\Microsoft\WindowsApps\",
                    StringComparison.OrdinalIgnoreCase,
                    "Only the known WindowsApps alias may be used.");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            decoyDirectory.Delete(recursive: true);
        }
    }

    /// <summary>An unregistered Sandbox package means the alias is not trusted, whatever it is.</summary>
    [TestMethod]
    public void ResolveTrustedAlias_WithoutTheSandboxPackage_ResolvesNothing()
    {
        Assert.IsNull(
            WindowsSandboxHostProbe.ResolveTrustedAlias(isPackageRegistered: () => false),
            "An alias whose package is not registered cannot be the Windows Sandbox client.");
    }

    /// <summary>
    /// A PATH-stripped process still finds the real alias, which is why PATH was consulted at all.
    /// </summary>
    [TestMethod]
    public void ResolveTrustedAlias_WithNoPathAtAll_StillFindsTheKnownAlias()
    {
        var originalPath = Environment.GetEnvironmentVariable("PATH");

        try
        {
            Environment.SetEnvironmentVariable("PATH", string.Empty);

            var resolved = WindowsSandboxHostProbe.ResolveTrustedAlias();

            if (resolved is null)
            {
                Assert.Inconclusive("wsb.exe is not installed on this machine.");
                return;
            }

            Assert.IsTrue(
                File.Exists(resolved),
                "A build agent or service with no PATH must still be able to use Windows Sandbox.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }

    [TestMethod]
    public async Task Commands_LaunchTheResolvedAbsolutePathNotABareName()
    {
        if (WindowsSandboxHostProbe.ResolveTrustedAlias() is null)
        {
            Assert.Inconclusive("wsb.exe is not installed on this machine.");
            return;
        }

        _runner.Result = new ProcessRunResult(0, """{ "WindowsSandboxEnvironments": [] }""", string.Empty);

        await _cli.ListAsync(TestContext.CancellationTokenSource.Token);

        var launched = _runner.Requests.Single().FileName;
        Assert.IsTrue(
            Path.IsPathFullyQualified(launched),
            $"Expected an absolute path, but winapp launched '{launched}'.");
    }

    [TestMethod]
    public async Task ExecuteAsync_WsbDiagnosticOnStderr_IsAnInfrastructureFailureNotAGuestExitCode()
    {
        // Regression: wsb exec never relays the guest's stdout or stderr, so anything on stderr is
        // wsb's own diagnostic and means the command was never dispatched. Returning that exit code
        // as a guest result lets an infrastructure failure impersonate an application outcome --
        // reproduced by passing a nonexistent Sandbox ID, which yields an HRESULT-like code.
        if (WindowsSandboxHostProbe.ResolveTrustedAlias() is null)
        {
            Assert.Inconclusive("wsb.exe is not installed on this machine.");
            return;
        }

        _runner.Result = new ProcessRunResult(
            unchecked((int)0x80070490),
            string.Empty,
            "Element not found. (0x80070490)");

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => _cli.ExecuteAsync("missing-id", "cmd /c exit 0", null, asSystem: false, TestContext.CancellationTokenSource.Token));

        Assert.AreEqual(ExecutionTargetErrorCodes.TransportFailed, failure.Error.Code);
        Assert.AreEqual("missing-id", failure.Error.Context!["sandboxId"]);
    }

    [TestMethod]
    public async Task ExecuteAsync_CleanDispatch_ReturnsTheGuestExitCode()
    {
        if (WindowsSandboxHostProbe.ResolveTrustedAlias() is null)
        {
            Assert.Inconclusive("wsb.exe is not installed on this machine.");
            return;
        }

        // The guest's exit code comes from wsb's --raw JSON, not from wsb's own exit code. Measured
        // live: a guest command that exits 42 leaves wsb itself exiting 0, so reading wsb's code
        // would report every failed guest command as a success.
        _runner.Result = new ProcessRunResult(0, """{"ExitCode":42}""", string.Empty);

        var exitCode = await _cli.ExecuteAsync(
            "sandbox-1", "cmd /c exit 42", null, asSystem: false, TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(42, exitCode);
    }

    [TestMethod]
    public async Task ExecuteAsync_PassesRunAsAndPreservesArgumentsAsSeparateValues()
    {
        if (WindowsSandboxHostProbe.ResolveTrustedAlias() is null)
        {
            Assert.Inconclusive("wsb.exe is not installed on this machine.");
            return;
        }

        _runner.Result = new ProcessRunResult(0, """{"ExitCode":0}""", string.Empty);

        await _cli.ExecuteAsync("sandbox-1", "cmd /c exit 0", @"C:\Work", asSystem: true, TestContext.CancellationTokenSource.Token);

        var arguments = _runner.Requests.Single().Arguments;

        // Values travel as their own list entries, so a path can never be smuggled in as an option.
        CollectionAssert.Contains(arguments.ToList(), "System");
        CollectionAssert.Contains(arguments.ToList(), @"C:\Work");
        CollectionAssert.Contains(arguments.ToList(), "cmd /c exit 0");
    }

    [TestMethod]
    public async Task ConnectAsync_StartsTheLongLivedInteractiveClientWithoutWaitingForExit()
    {
        if (WindowsSandboxHostProbe.ResolveTrustedAlias() is null)
        {
            Assert.Inconclusive("wsb.exe is not installed on this machine.");
            return;
        }

        System.Diagnostics.ProcessStartInfo? captured = null;
        _cli.ConnectLauncher = startInfo =>
        {
            captured = startInfo;
            return null;
        };

        await _cli.ConnectAsync("sandbox-1", TestContext.CancellationTokenSource.Token);

        Assert.IsNotNull(captured);
        Assert.IsFalse(captured.UseShellExecute);
        Assert.IsTrue(captured.CreateNoWindow);
        Assert.AreEqual(System.Diagnostics.ProcessWindowStyle.Hidden, captured.WindowStyle);
        Assert.IsFalse(captured.RedirectStandardInput);
        Assert.IsFalse(captured.RedirectStandardOutput);
        Assert.IsFalse(captured.RedirectStandardError);
        CollectionAssert.AreEqual(
            ConnectArguments,
            captured.ArgumentList.ToArray());
    }

    [TestMethod]
    public async Task ConnectAsync_ReportsOwnershipBeforeWatchingForImmediateFailure()
    {
        _cli.UseExecutable(Path.Join(Environment.SystemDirectory, "wsb.exe"));
        System.Diagnostics.Process? launched = null;
        _cli.ConnectLauncher = _ =>
        {
            launched = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec")!,
                Arguments = "/d /c ping 127.0.0.1 -n 6 >nul",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            return launched;
        };

        try
        {
            SandboxConnectAttempt? observed = null;
            var connect = _cli.ConnectAsync(
                "sandbox-1",
                attempt => observed = attempt,
                TestContext.CancellationTokenSource.Token);

            Assert.IsNotNull(observed);
            Assert.AreEqual(launched!.Id, observed.Ownership!.LauncherProcessId);
            Assert.IsFalse(connect.IsCompleted);

            var attempt = await connect;
            if (!launched.HasExited)
            {
                launched.Kill(entireProcessTree: true);
                await launched.WaitForExitAsync(TestContext.CancellationTokenSource.Token);
            }

            attempt.Dispose();
            launched = null;
        }
        finally
        {
            if (launched is not null)
            {
                try
                {
                    if (!launched.HasExited)
                    {
                        launched.Kill(entireProcessTree: true);
                        await launched.WaitForExitAsync(TestContext.CancellationTokenSource.Token);
                    }
                }
                catch (InvalidOperationException)
                {
                    // The connect attempt already released the process handle.
                }

                launched.Dispose();
            }
        }
    }

    [TestMethod]
    public async Task ConnectAsync_StillDetectsAnImmediateNonzeroExit()
    {
        _cli.UseExecutable(Path.Join(Environment.SystemDirectory, "wsb.exe"));
        _cli.ConnectLauncher = _ => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec")!,
            Arguments = "/d /c exit 17",
            UseShellExecute = false,
            CreateNoWindow = true,
        });

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => _cli.ConnectAsync("sandbox-1", TestContext.CancellationTokenSource.Token));

        Assert.AreEqual(ExecutionTargetErrorCodes.NoInteractiveSession, failure.Error.Code);
    }

    [TestMethod]
    public async Task LaunchAgentAsync_KeepsAnAwaitedWsbOperationBehindTheHeartbeat()
    {
        if (WindowsSandboxHostProbe.ResolveTrustedAlias() is null)
        {
            Assert.Inconclusive("wsb.exe is not installed on this machine.");
            return;
        }

        await _cli.LaunchAgentAsync(
            "sandbox-1",
            @"""C:\WinAppBootstrap\winapp.exe"" guest-agent",
            TestContext.CancellationTokenSource.Token);

        var request = _runner.Requests.Single();
        CollectionAssert.Contains(request.Arguments.ToList(), "ExistingLogin");
        CollectionAssert.Contains(request.Arguments.ToList(), @"""C:\WinAppBootstrap\winapp.exe"" guest-agent");
    }

    /// <summary>MSTest injects this; used for per-test cancellation.</summary>
    public TestContext TestContext { get; set; } = null!;
}
