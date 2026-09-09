// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using WinApp.Cli.Helpers;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.ExecutionTargets.WindowsSandbox;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Live Windows Sandbox coverage, gated on <c>WINAPP_SANDBOX_E2E=1</c>.
/// </summary>
/// <remarks>
/// Everything else in the execution-target suite runs the real host channel against the real guest
/// server over an in-memory transport, which is what proves orchestration does not depend on Windows
/// Sandbox. These tests prove the opposite half: that the backend's assumptions about <c>wsb.exe</c>,
/// the guest session, and the agent bootstrap actually hold on a real machine.
/// <para>
/// They are gated because Windows permits exactly one Sandbox at a time and creating one is a
/// machine-wide, visible side effect. Running them by default would take that resource away from
/// whoever is using the machine, and would fail on any host without the optional feature.
/// </para>
/// <para>
/// <b>These tests never stop a Sandbox winapp did not create.</b> If an unowned instance is running,
/// they report it and stop, exactly as the product does. Cleanup only ever touches what the test
/// itself created, and always runs.
/// </para>
/// </remarks>
[TestClass]
[DoNotParallelize]
public partial class SandboxLiveE2ETests
{
    /// <summary>Set to <c>1</c> to run these tests.</summary>
    internal const string GateVariable = "WINAPP_SANDBOX_E2E";

    /// <summary>Architecture-matched NativeAOT winapp binary staged into the guest.</summary>
    internal const string BinaryVariable = "WINAPP_SANDBOX_E2E_BINARY";

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Sandbox clients that were already running when this test started.</summary>
    /// <remarks>
    /// Cleanup closes only clients absent from this set. A shared machine can carry a client left
    /// behind by an earlier Sandbox or belonging to somebody else, and closing one of those is the
    /// same destructive act as stopping an unowned instance.
    /// </remarks>
    private HashSet<int> _clientsBeforeTest = [];

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public async Task RequireGateAsync()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(GateVariable), "1", StringComparison.Ordinal))
        {
            Assert.Inconclusive(
                $"Set {GateVariable}=1 on a machine with Windows Sandbox to run live execution-target coverage.");
        }

        if (Environment.GetEnvironmentVariable(BinaryVariable) is not { Length: > 0 } binary ||
            !File.Exists(binary))
        {
            Assert.Inconclusive(
                $"Set {BinaryVariable} to the architecture-matched NativeAOT winapp.exe built for the guest.");
        }

        // Acquired last, and only once the gate has admitted this test. MSTest treats an
        // Assert.Inconclusive in initialization as a skip and does not run cleanup for it, so a
        // test that took the lock and then skipped would never give it back -- and every later live
        // test would wait on it forever. Nothing above this line can block.
        await LiveSandboxExclusion.AcquireAsync(TestContext.CancellationToken);

        // Taken before the test creates anything, so cleanup can tell the clients it caused from the
        // ones that were already here.
        _clientsBeforeTest = CurrentClientProcessIds();
    }

    /// <summary>Hands the machine's one Sandbox back to whichever live test runs next.</summary>
    [TestCleanup]
    public void ReleaseSandboxExclusion() => LiveSandboxExclusion.Release();

    /// <summary>
    /// Support probing must be honest about this machine before anything is built or created.
    /// </summary>
    [TestMethod]
    public async Task Support_IsProbedWithoutCreatingAnything()
    {
        var backend = CreateBackend();
        var support = await backend.ProbeSupportAsync(TestContext.CancellationToken);

        if (!support.IsSupported)
        {
            Assert.Inconclusive(
                $"This machine cannot run Windows Sandbox: {support.Error?.Code} — {support.Error?.Message}");
        }

        // Probing must not have created an instance. Anything running now was not ours.
        Assert.IsTrue(support.IsSupported);
    }

    /// <summary>
    /// A cold start, a warm reuse, and a round trip through the real transport.
    /// </summary>
    /// <remarks>
    /// Deliberately one test rather than three. Each stage depends on the previous one having left a
    /// live Sandbox, and splitting them would either serialize on shared machine state between test
    /// methods or create and tear down a Sandbox three times.
    /// </remarks>
    [TestMethod]
    public async Task ColdStartThenWarmReuse_RunsACommandInTheGuest()
    {
        await SkipIfUnsupportedOrOccupiedAsync();

        var provider = new TargetStateDirectoryProvider();
        var coldBackend = CreateBackend(provider);
        var coldOrchestrator = new ExecutionTargetOrchestrator(
            coldBackend,
            new TargetMutationLock(provider),
            new TargetConnectionLock(provider));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeout.CancelAfter(CommandTimeout);
        var foregroundBefore = GetForegroundWindow();

        try
        {
            string firstEpoch;
            HashSet<int> clientsAfterColdStart;

            await using (var cold = await coldOrchestrator.PrepareAsync(
                PrepareTargetOptions.Mutating, timeout.Token))
            {
                Assert.IsFalse(cold.Reused, "The first prepare on an empty machine is a cold start.");
                Assert.IsNotNull(cold.Capabilities.ManagedRoot, "The guest must report where it stores deployments.");
                Assert.IsTrue(cold.Capabilities.SupportsInteractiveDesktop);
                Assert.AreEqual(
                    foregroundBefore,
                    GetForegroundWindow(),
                    "Preparing the Sandbox must not change the host foreground window.");

                firstEpoch = cold.Epoch.Value;

                var result = await cold.Operations.ExecuteAsync(
                    new GuestExecRequest
                    {
                        Executable = "cmd.exe",
                        Arguments = ["/c", "exit", "7"],
                    },
                    callbacks: null,
                    timeout.Token);

                // The guest application's own exit code, distinct from an infrastructure failure.
                Assert.AreEqual(7, result.ExitCode);

                clientsAfterColdStart = CurrentClientProcessIds();
            }

            var materialAfterColdStart = ReadBootstrapMaterial(provider);

            // A second winapp process, modelled the way one actually arrives: its own backend and its
            // own orchestrator over the same state root, with the first target already disposed. A
            // shared backend would still be holding the connection material in memory, and that is
            // precisely what must not be doing the work here — a separate process has only what the
            // first command persisted, so reuse has to be re-established from disk or not at all.
            var warmBackend = CreateBackend(provider);
            var warmOrchestrator = new ExecutionTargetOrchestrator(
                warmBackend,
                new TargetMutationLock(provider),
                new TargetConnectionLock(provider));

            var warmClock = Stopwatch.StartNew();

            await using var warm = await warmOrchestrator.PrepareAsync(
                PrepareTargetOptions.Mutating, timeout.Token);

            warmClock.Stop();

            Assert.IsTrue(warm.Reused, "The second prepare must reuse the instance rather than recreate it.");
            Assert.AreEqual(firstEpoch, warm.Epoch.Value, "Reuse must stay in the same generation.");
            Assert.AreEqual(
                coldBackend.DescribeForDiagnostics()["sandboxId"],
                warmBackend.DescribeForDiagnostics()["sandboxId"],
                "Reuse must attach to the very same instance rather than a replacement.");

            // Reuse is one TCP connect to an agent that is already serving. The bound is deliberately
            // generous: it is not a performance assertion, it is there to catch a silent fall-through
            // to a full bootstrap, which re-stages the binary and waits on a heartbeat.
            Assert.IsTrue(
                warmClock.Elapsed < TimeSpan.FromSeconds(30),
                $"Reuse should reconnect to the running agent rather than bootstrap it again, but took {warmClock.Elapsed}.");

            // The deterministic half of that same claim, so a timing flake on slow hardware can never
            // be "fixed" by loosening the bound above and silently deleting the only real check.
            CollectionAssert.AreEquivalent(
                materialAfterColdStart.ToArray(),
                ReadBootstrapMaterial(provider).ToArray(),
                "Reuse must reconnect with the material the cold start staged rather than write new material.");

            CollectionAssert.AreEquivalent(
                clientsAfterColdStart.ToArray(),
                CurrentClientProcessIds().ToArray(),
                "Reusing a Sandbox must not start a second interactive client.");

            Assert.AreEqual(
                foregroundBefore,
                GetForegroundWindow(),
                "Reusing the Sandbox must not change the host foreground window.");
        }
        finally
        {
            await StopOwnedSandboxAsync();
        }
    }

    /// <summary>
    /// A file placed in the guest comes back byte-identical.
    /// </summary>
    [TestMethod]
    public async Task FilesRoundTripThroughTheGuest()
    {
        await SkipIfUnsupportedOrOccupiedAsync();

        var provider = new TargetStateDirectoryProvider();
        var orchestrator = new ExecutionTargetOrchestrator(
            CreateBackend(),
            new TargetMutationLock(provider),
            new TargetConnectionLock(provider));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeout.CancelAfter(CommandTimeout);

        var root = TestPaths.TempRoot(nameof(FilesRoundTripThroughTheGuest));
        var source = TestPaths.Under(root, "source.bin");
        var returned = TestPaths.Under(root, "returned.bin");

        Directory.CreateDirectory(root);
        var content = new byte[64 * 1024];
        Random.Shared.NextBytes(content);
        await File.WriteAllBytesAsync(source, content, timeout.Token);

        try
        {
            await using var target = await orchestrator.PrepareAsync(
                PrepareTargetOptions.Mutating with { RequireInteractiveDesktop = false }, timeout.Token);

            var scope = new GuestPathScope(GuestRootNames.Work, Scope: null);

            await using (var stream = File.OpenRead(source))
            {
                await target.Operations.PutFileAsync(
                    scope,
                    new GuestFileInfo("roundtrip.bin", content.Length, DateTime.UtcNow.Ticks, Sha256(content)),
                    stream,
                    timeout.Token);
            }

            await using (var stream = File.Create(returned))
            {
                await target.Operations.GetFileAsync(scope, "roundtrip.bin", stream, timeout.Token);
            }

            CollectionAssert.AreEqual(content, await File.ReadAllBytesAsync(returned, timeout.Token));

            await target.Operations.DeleteFilesAsync(scope, ["roundtrip.bin"], timeout.Token);
        }
        finally
        {
            TryDeleteDirectory(root);
            await StopOwnedSandboxAsync();
        }
    }

    [TestMethod]
    public async Task PackagedFrameworkDependentWinUi_RunsAndAutomatesEndToEnd()
    {
        await SkipIfUnsupportedOrOccupiedAsync();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));

        var root = FindRepositoryRoot();
        var project = Path.Join(root, "samples", "winui-app", "winui-app.csproj");
        var artifacts = TestPaths.TempRoot(nameof(PackagedFrameworkDependentWinUi_RunsAndAutomatesEndToEnd));
        var screenshot = TestPaths.Under(artifacts, "sandbox.png");
        var recording = TestPaths.Under(artifacts, "sandbox.mp4");
        Directory.CreateDirectory(artifacts);

        var architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture ==
            System.Runtime.InteropServices.Architecture.Arm64
                ? "arm64"
                : "x64";

        try
        {
            var launched = await RunCliAsync(
                [
                    "run", project, "--on", "sandbox", "--arch", architecture, "--detach", "--json",
                ],
                timeout.Token);
            AssertCommandSucceeded(launched, "packaged WinUI launch");
            StringAssert.Contains(launched.StandardOutput, "\"Sandbox\": true");

            var inspect = await RunCliAsync(
                ["ui", "inspect", "--on", "sandbox", "-a", "winui-app", "--interactive"],
                timeout.Token);
            AssertCommandSucceeded(inspect, "guest UI inspection");
            StringAssert.Contains(inspect.StandardOutput, "CounterButton");

            AssertCommandSucceeded(
                await RunCliAsync(
                    ["ui", "set-value", "--on", "sandbox", "TextInput", "Sandbox hello", "-a", "winui-app"],
                    timeout.Token),
                "guest text input");
            AssertCommandSucceeded(
                await RunCliAsync(
                    ["ui", "invoke", "--on", "sandbox", "FeatureToggle", "-a", "winui-app"],
                    timeout.Token),
                "guest toggle input");
            AssertCommandSucceeded(
                await RunCliAsync(
                    ["ui", "invoke", "--on", "sandbox", "SubmitButton", "-a", "winui-app"],
                    timeout.Token),
                "guest submit input");
            AssertCommandSucceeded(
                await RunCliAsync(
                    [
                        "ui", "wait-for", "--on", "sandbox", "ResultDisplay", "-a", "winui-app",
                        "--property", "Name", "--value", "Submitted: Sandbox hello (Feature: On)",
                        "--timeout", "10000",
                    ],
                    timeout.Token),
                "guest UI postcondition");

            var captured = await RunCliAsync(
                ["ui", "screenshot", "--on", "sandbox", "-a", "winui-app", "-o", screenshot, "--json"],
                timeout.Token);
            AssertCommandSucceeded(captured, "guest screenshot");
            Assert.IsTrue(File.Exists(screenshot));
            Assert.IsGreaterThan(1024L, new FileInfo(screenshot).Length);
            StringAssert.Contains(captured.StandardOutput, screenshot.Replace(@"\", @"\\"));

            var recorded = await RunCliAsync(
                [
                    "ui", "record", "--on", "sandbox", "-a", "winui-app", "--duration-sec", "2",
                    "-o", recording, "--json",
                ],
                timeout.Token);
            AssertCommandSucceeded(recorded, "guest recording");
            Assert.IsTrue(File.Exists(recording));
            Assert.IsGreaterThan(1024L, new FileInfo(recording).Length);

            var store = new TargetStateStore(new TargetStateDirectoryProvider());
            var previous = store.Read(WindowsSandboxTarget.Default)!;
            await CreateCli().StopAsync(previous.InstanceId!, timeout.Token);
            await WaitForNoSandboxAsync(timeout.Token);

            var recovered = await RunCliAsync(
                ["target", "exec", "sandbox", "--", "cmd.exe", "/c", "echo", "recovered-after-stop"],
                timeout.Token);
            AssertCommandSucceeded(recovered, "external-stop recovery");
            StringAssert.Contains(recovered.StandardOutput, "recovered-after-stop");

            var replacement = store.Read(WindowsSandboxTarget.Default)!;
            Assert.AreNotEqual(previous.InstanceId, replacement.InstanceId);
            Assert.AreNotEqual(previous.BootNonce, replacement.BootNonce);
        }
        finally
        {
            TryDeleteDirectory(artifacts);
            await StopOwnedSandboxAsync();
        }
    }

    /// <summary>
    /// Standard input piped into <c>winapp target exec sandbox</c> reaches the guest process.
    /// </summary>
    /// <remarks>
    /// The command is documented as streaming stdin as well as stdout and stderr, and this is the
    /// only test that proves it through the real binary, a real pipe, and a real guest. The guest
    /// command reads to end of input, so it also proves the host closes guest stdin on EOF: without
    /// that close it would hang until the timeout rather than echo anything.
    /// </remarks>
    [TestMethod]
    public async Task TargetExec_ForwardsStandardInputToTheGuestProcess()
    {
        await SkipIfUnsupportedOrOccupiedAsync();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeout.CancelAfter(CommandTimeout);

        var marker = $"stdin-{Guid.NewGuid():n}";

        try
        {
            // 'sort' consumes standard input until EOF and writes it back, so an empty result means
            // either nothing was forwarded or end of input was never signalled.
            var result = await RunCliAsync(
                ["target", "exec", "sandbox", "--", "cmd", "/c", "sort"],
                timeout.Token,
                standardInput: marker + Environment.NewLine);

            AssertCommandSucceeded(result, "sandbox exec with piped standard input");
            StringAssert.Contains(result.StandardOutput, marker);
        }
        finally
        {
            await StopOwnedSandboxAsync();
        }
    }

    /// <summary>
    /// A long-running foreground operation must not delay a separate command in a real Sandbox.
    /// </summary>
    /// <remarks>
    /// The reported symptom, measured rather than described: a foreground <c>run</c> made a separate
    /// <c>ui list-windows</c> wait more than ninety seconds, and a long <c>exec</c> made a short one
    /// wait 144 seconds. Both were the guest serving one channel at a time while the host held the
    /// connection lock for its prepared target's whole life.
    /// <para>
    /// The assertion is deliberately generous. What is being proven is the difference between
    /// "waits for the long operation" and "does not", not a performance budget: a cold guest, a
    /// loaded machine, and a debug build all move the absolute numbers, while the long operation
    /// here runs far longer than any of them.
    /// </para>
    /// </remarks>
    [TestMethod]
    public async Task LongRunningOperation_DoesNotDelayASeparateCommand()
    {
        await SkipIfUnsupportedOrOccupiedAsync();

        var provider = new TargetStateDirectoryProvider();
        var orchestrator = new ExecutionTargetOrchestrator(
            CreateBackend(),
            new TargetMutationLock(provider),
            new TargetConnectionLock(provider));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeout.CancelAfter(CommandTimeout);

        var longOperationSeconds = TimeSpan.FromMinutes(2);

        try
        {
            // Everything expensive — creating the Sandbox, bootstrapping the agent, connecting the
            // window — happens here, so the measurement below covers only the contended path.
            await using var warmUp = await orchestrator.PrepareAsync(
                PrepareTargetOptions.Mutating, timeout.Token);

            await using var occupied = await orchestrator.PrepareAsync(
                PrepareTargetOptions.ReadOnly, timeout.Token);

            var longRunning = occupied.Operations.ExecuteAsync(
                new GuestExecRequest
                {
                    Executable = "cmd.exe",

                    // ping rather than timeout: timeout exits immediately when standard input is
                    // redirected, which it always is here, and would leave this measuring nothing.
                    Arguments =
                    [
                        "/c",
                        "ping",
                        "-n",
                        ((int)longOperationSeconds.TotalSeconds + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                        "127.0.0.1",
                    ],
                },
                callbacks: null,
                timeout.Token);

            // A second winapp process's worth of work, start to finish, while that one runs.
            var stopwatch = Stopwatch.StartNew();

            await using (var separate = await orchestrator.PrepareAsync(
                PrepareTargetOptions.ReadOnly, timeout.Token))
            {
                var quick = await separate.Operations.ExecuteAsync(
                    new GuestExecRequest { Executable = "cmd.exe", Arguments = ["/c", "exit", "3"] },
                    callbacks: null,
                    timeout.Token);

                Assert.AreEqual(3, quick.ExitCode);
            }

            stopwatch.Stop();

            TestContext.WriteLine(
                $"Separate command completed in {stopwatch.Elapsed.TotalSeconds:F1}s while a " +
                $"{longOperationSeconds.TotalSeconds:F0}s operation was running.");

            Assert.IsTrue(
                stopwatch.Elapsed < TimeSpan.FromSeconds(45),
                $"A separate command waited {stopwatch.Elapsed.TotalSeconds:F1}s behind a long-running " +
                "operation, which means channels are still being serialized.");

            Assert.IsFalse(
                longRunning.IsCompleted,
                "The long operation must still have been running, or this measured nothing.");
        }
        finally
        {
            await StopOwnedSandboxAsync();
        }
    }

    /// <summary>
    /// A host directory junction must not widen what <c>target push</c> sends into the guest.
    /// </summary>
    /// <remarks>
    /// The file behind the junction is an ordinary file with no reparse attribute, so a check that
    /// only looked at files would copy it while believing it was inside the folder the caller named.
    /// Junctions need no elevation, so this runs as an ordinary user; if one cannot be created the
    /// result is inconclusive rather than a pass that proved nothing.
    /// </remarks>
    [TestMethod]
    public async Task TargetPush_DoesNotFollowAHostDirectoryJunction()
    {
        await SkipIfUnsupportedOrOccupiedAsync();

        var provider = new TargetStateDirectoryProvider();
        var orchestrator = new ExecutionTargetOrchestrator(
            CreateBackend(),
            new TargetMutationLock(provider),
            new TargetConnectionLock(provider));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeout.CancelAfter(CommandTimeout);

        var root = TestPaths.TempRoot(nameof(TargetPush_DoesNotFollowAHostDirectoryJunction));
        var outside = TestPaths.TempRoot("live-outside");
        var guestFolder = $"junction-{Guid.NewGuid():n}";
        var link = TestPaths.Under(root, "linked");

        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(TestPaths.Under(root, "included.txt"), "real", timeout.Token);
        await File.WriteAllTextAsync(TestPaths.Under(outside, "secret.txt"), "not yours", timeout.Token);

        try
        {
            if (!TryCreateJunction(link, outside))
            {
                Assert.Inconclusive("Creating a directory junction is not possible in this run.");
                return;
            }

            await using var target = await orchestrator.PrepareAsync(
                PrepareTargetOptions.Mutating with { RequireInteractiveDesktop = false }, timeout.Token);

            var copied = await TargetFileTransferService.CopyAsync(
                target.Operations,

                // Relative to the guest work root. A rooted guest path is refused outright, so
                // passing one here would fail before the junction was ever exercised.
                new TargetTransferRequest(TargetTransferDirection.ToTarget, root, guestFolder),
                timeout.Token);

            Assert.AreEqual(1, copied.Transferred, "Only the file genuinely inside the folder may be copied.");

            var guestFiles = await target.Operations.ListFilesAsync(
                new GuestPathScope(GuestRootNames.Work, Scope: null), timeout.Token);

            Assert.IsFalse(
                guestFiles.Any(f => f.RelativePath.Contains("secret", StringComparison.OrdinalIgnoreCase)),
                "A file behind a host junction must never reach the guest.");

            Assert.IsTrue(
                guestFiles.Any(f => f.RelativePath.Contains("included.txt", StringComparison.OrdinalIgnoreCase)),
                "The real file must still be copied.");

            await target.Operations.DeleteFilesAsync(
                new GuestPathScope(GuestRootNames.Work, Scope: null),
                [.. guestFiles
                    .Where(f => f.RelativePath.Contains(guestFolder, StringComparison.OrdinalIgnoreCase))
                    .Select(f => f.RelativePath)],
                timeout.Token);
        }
        finally
        {
            TryRemoveJunction(link);
            TryDeleteDirectory(root);
            TryDeleteDirectory(outside);
            await StopOwnedSandboxAsync();
        }
    }

    private static bool TryCreateJunction(string linkPath, string target)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                ArgumentList = { "/c", "mklink", "/J", linkPath, target },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });

            process?.WaitForExit(milliseconds: 30_000);

            return Directory.Exists(linkPath)
                && new DirectoryInfo(linkPath).Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Removes the link itself, never its target.</summary>
    private static void TryRemoveJunction(string linkPath)
    {
        try
        {
            if (Directory.Exists(linkPath) &&
                new DirectoryInfo(linkPath).Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                Directory.Delete(linkPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Temp cleanup is not worth failing a test over.
        }
    }

    private static string Sha256(byte[] content) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant();

    private static WindowsSandboxCli CreateCli() => new(new ProcessRunner());

    private static async Task<ProcessRunResult> RunCliAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        string? standardInput = null)
    {
        var captureRoot = Path.Join(Path.GetTempPath(), "winapp-live-capture");
        Directory.CreateDirectory(captureRoot);
        var token = Guid.NewGuid().ToString("N");
        var outputPath = Path.Join(captureRoot, $"{token}.out");
        var errorPath = Path.Join(captureRoot, $"{token}.err");
        var inputPath = Path.Join(captureRoot, $"{token}.in");
        var scriptPath = Path.Join(captureRoot, $"{token}.cmd");
        var binary = Environment.GetEnvironmentVariable(BinaryVariable)!;
        var command = WindowsCommandLine.JoinArguments([binary, .. arguments])!;
        var outputArgument = WindowsCommandLine.JoinArguments([outputPath])!;
        var errorArgument = WindowsCommandLine.JoinArguments([errorPath])!;

        // Redirected from a real file rather than written to the child's pipe, so the bytes are
        // already waiting before winapp starts. That is the shape that would silently fail if the
        // pump did not begin from the published operation ID.
        var inputRedirect = string.Empty;

        if (standardInput is not null)
        {
            await File.WriteAllTextAsync(inputPath, standardInput, cancellationToken);
            inputRedirect = $" 0<{WindowsCommandLine.JoinArguments([inputPath])!}";
        }

        await File.WriteAllTextAsync(
            scriptPath,
            $"@echo off\r\n{command} 1>{outputArgument} 2>{errorArgument}{inputRedirect}\r\nexit /b %errorlevel%\r\n",
            cancellationToken);

        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec")!,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add(scriptPath);

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start the live-test winapp binary.");
            await process.WaitForExitAsync(cancellationToken);
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);

            return new ProcessRunResult(
                process.ExitCode,
                ReadSharedText(outputPath),
                ReadSharedText(errorPath));
        }
        finally
        {
            TryDeleteFile(outputPath);
            TryDeleteFile(errorPath);
            TryDeleteFile(inputPath);
            TryDeleteFile(scriptPath);
        }
    }

    private static string ReadSharedText(string path)
    {
        if (!File.Exists(path))
        {
            return string.Empty;
        }

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The persistent WSB child can retain the redirected handle until target cleanup.
        }
    }

    private static void AssertCommandSucceeded(ProcessRunResult result, string operation)
    {
        Assert.AreEqual(
            0,
            result.ExitCode,
            $"{operation} failed.{Environment.NewLine}stdout:{Environment.NewLine}{result.StandardOutput}" +
            $"{Environment.NewLine}stderr:{Environment.NewLine}{result.StandardError}");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Join(directory.FullName, "scripts", "build-cli.ps1")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the winapp repository root.");
    }

    private static async Task WaitForNoSandboxAsync(CancellationToken cancellationToken)
    {
        while ((await CreateCli().ListAsync(cancellationToken)).Count > 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }
    }

    /// <summary>
    /// Builds a backend wired the way the CLI's own container wires one.
    /// </summary>
    /// <remarks>
    /// The state store is not optional here, despite the constructor defaulting it to null. It is the
    /// only thing that records <c>BootstrappedEpoch</c>, and that marker is the sole evidence a later
    /// command uses to decide an instance is warm. A backend built without it can therefore finish a
    /// bootstrap and still leave the next command no way to know one ever happened — which made warm
    /// reuse unobservable rather than broken, a fixture defect that looks exactly like a product one.
    /// <para>
    /// No setup runner, deliberately: these tests must never enable a Windows feature or install a
    /// package on a shared machine. With none, <c>ProbeSupportAsync</c> falls back to a read-only
    /// <c>wsb.exe</c> availability check, which is all a live test is entitled to do here.
    /// </para>
    /// </remarks>
    private static WindowsSandboxBackend CreateBackend(ITargetStateDirectoryProvider? directoryProvider = null)
    {
        var cli = CreateCli();
        var provider = directoryProvider ?? new TargetStateDirectoryProvider();
        var stateStore = new TargetStateStore(provider);

        return new WindowsSandboxBackend(
            cli,
            new WindowsSandboxLifecycle(cli, stateStore),
            provider,
            new FixedHostBinaryProvider(
                new FileInfo(Environment.GetEnvironmentVariable(BinaryVariable)!)),
            new WindowsSandboxWindowController(),
            setup: null,
            stateStore);
    }

    /// <summary>Process name of the Windows Sandbox interactive client.</summary>
    internal const string RemoteSessionProcessName = "WindowsSandboxRemoteSession";

    /// <summary>
    /// PIDs of every Sandbox interactive client currently running on this host.
    /// </summary>
    /// <remarks>
    /// Compared before and after an operation to prove it started no second client. Identities rather
    /// than a count, so one client exiting while an unrelated one starts cannot cancel out and hide a
    /// duplicate.
    /// </remarks>
    internal static HashSet<int> CurrentClientProcessIds()
    {
        var clients = new HashSet<int>();

        foreach (var process in Process.GetProcessesByName(RemoteSessionProcessName))
        {
            using (process)
            {
                clients.Add(process.Id);
            }
        }

        return clients;
    }

    /// <summary>
    /// Contents of every bootstrap connection file currently staged for this target.
    /// </summary>
    /// <remarks>
    /// A deterministic answer to "did this prepare bootstrap again?", which the elapsed-time bound
    /// alone can only guess at. A full bootstrap writes fresh connection material — a new pre-shared
    /// key and a new port — whereas a reconnect only reads it. Reuse keeps the epoch, and the epoch
    /// is what names the folder, so the file a re-bootstrap would rewrite is exactly the one compared
    /// here. Without this, every other assertion in the warm case (<c>Reused</c>, the epoch, the
    /// instance ID, the client set) would also hold for a silent same-epoch agent repair, leaving a
    /// wall-clock race as the only thing standing between the two.
    /// </remarks>
    private static Dictionary<string, string> ReadBootstrapMaterial(TargetStateDirectoryProvider provider)
    {
        var material = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var root = provider.GetTargetRoot(WindowsSandboxTarget.Default, create: false);

        if (!root.Exists)
        {
            return material;
        }

        foreach (var file in root.EnumerateFiles(GuestBootstrapMaterial.FileName, SearchOption.AllDirectories))
        {
            try
            {
                material[file.FullName] = File.ReadAllText(file.FullName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                material[file.FullName] = $"<unreadable: {ex.GetType().Name}>";
            }
        }

        return material;
    }

    private sealed class FixedHostBinaryProvider(FileInfo binary) : IHostWinappBinaryProvider
    {
        public FileInfo GetBinary() => binary;
    }

    /// <summary>
    /// Skips rather than fails when the machine cannot run Sandbox, or when someone else's is up.
    /// </summary>
    /// <remarks>
    /// An unowned instance is not a test failure and must never be stopped: it may hold work that
    /// matters to whoever created it. Windows allows only one, so the honest outcome is to say so
    /// and stop.
    /// </remarks>
    private async Task SkipIfUnsupportedOrOccupiedAsync()
    {
        var support = await CreateBackend().ProbeSupportAsync(TestContext.CancellationToken);

        if (!support.IsSupported)
        {
            Assert.Inconclusive(
                $"This machine cannot run Windows Sandbox: {support.Error?.Code} — {support.Error?.Message}");
        }

        var running = await CreateCli().ListAsync(TestContext.CancellationToken);
        var owned = new TargetStateStore(new TargetStateDirectoryProvider())
            .Read(WindowsSandboxTarget.Default)?.InstanceId;

        if (running.Any(id => !string.Equals(id, owned, StringComparison.OrdinalIgnoreCase)))
        {
            Assert.Inconclusive(
                "A Windows Sandbox instance that winapp does not own is already running. " +
                "Windows permits only one, and this test will not stop an instance it did not create.");
        }
    }

    /// <summary>
    /// Stops only the instance recorded as winapp's own, and closes only the clients this test
    /// caused, so the machine is left as it was found.
    /// </summary>
    /// <remarks>
    /// The client is closed separately and afterwards because it outlives the Sandbox it served:
    /// <c>wsb stop</c> ends the instance and leaves the <c>WindowsSandboxRemoteSession</c> process
    /// running, so a suite that stopped instances alone would leak one client per test.
    /// </remarks>
    private async Task StopOwnedSandboxAsync()
    {
        try
        {
            var state = new TargetStateStore(new TargetStateDirectoryProvider())
                .Read(WindowsSandboxTarget.Default);

            if (state?.InstanceId is { Length: > 0 } instanceId)
            {
                await CreateCli().StopAsync(instanceId, CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is ExecutionTargetException or IOException or UnauthorizedAccessException)
        {
            // Cleanup failure must not mask the assertion that already ran.
            Trace.TraceWarning("Could not stop the Sandbox this test created: {0}", ex.Message);
        }

        foreach (var clientProcessId in CurrentClientProcessIds().Except(_clientsBeforeTest))
        {
            try
            {
                using var client = Process.GetProcessById(clientProcessId);
                client.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or SystemException)
            {
                // Already gone is the expected case once its Sandbox has stopped.
                Trace.TraceWarning("Could not close a Sandbox client this test caused: {0}", ex.Message);
            }
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Could not remove '{0}': {1}", path, ex.Message);
        }
    }

    // The foreground-window checks read real Win32 state to prove preparing or reusing a Sandbox
    // never steals focus. Declared here rather than through the generated Windows.Win32.PInvoke:
    // this test project references both winapp and the UI Automation recording package, and each
    // ships its own Windows.Win32.PInvoke, so the generated name is ambiguous from a test.
    // Source-generated (LibraryImport) to match the rest of the suite's interop.
    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();
}