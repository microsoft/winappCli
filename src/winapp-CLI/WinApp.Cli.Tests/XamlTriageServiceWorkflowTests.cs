// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Workflow/orchestration tests for <see cref="XamlTriageService.TryAnalyzeAsync"/> and the
/// process-execution helpers, driven through the internal test seams (binaries resolver, extension
/// download/hash, child-process factory, and timeout override) so no real signed debugger binaries,
/// network downloads, or debugging engine are involved. Marked <c>[DoNotParallelize]</c> because the
/// tests mutate the process-wide <c>WINAPP_DBGTOOLS_DIR</c> environment variable and the static
/// <see cref="XamlTriageService.TriageTimeoutOverride"/> and
/// <see cref="XamlTriageService.ProcessPathProvider"/> seams.
/// </summary>
/// <remarks>
/// <para><b>Documented coverage ceiling (~96% Debug line coverage across the service).</b> The few
/// remaining uncovered lines are OS/network boundaries or defensive guards, left honestly uncovered per
/// policy rather than forced or excluded:</para>
/// <list type="bullet">
///   <item>298-300 — <c>Process.Start</c> returning a null/already-exited handle: the OS declining to start
///   a process that was accepted, not reproducible on demand.</item>
///   <item>393-394, 396 — the <c>TryKill</c> catch: best-effort teardown of a child that races its own
///   exit; defensive.</item>
///   <item>440-443 — the real HTTPS download of <c>winui-dbgext.js</c> from GitHub: the network boundary,
///   replaced by the <c>ExtensionBytesDownloader</c> seam in every test.</item>
/// </list>
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class XamlTriageServiceWorkflowTests
{
    private string _tempRoot = null!;
    private string? _savedEnvOverride;

    [TestInitialize]
    public void Setup()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "winapp-xamltriage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _savedEnvOverride = Environment.GetEnvironmentVariable(XamlTriageBinaries.EnvOverride);
        // Default to no override so IsEnvOverrideSet is false unless a test opts in.
        Environment.SetEnvironmentVariable(XamlTriageBinaries.EnvOverride, null);
    }

    [TestCleanup]
    public void Cleanup()
    {
        Environment.SetEnvironmentVariable(XamlTriageBinaries.EnvOverride, _savedEnvOverride);
        XamlTriageService.TriageTimeoutOverride = null;
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // best effort temp cleanup
        }
    }

    private XamlTriageService CreateService()
    {
        var dirService = new FakeWinappDirectoryService(new DirectoryInfo(_tempRoot));
        var nuget = new FakeNugetService { CacheDirectory = new DirectoryInfo(Path.Combine(_tempRoot, "nuget")) };
        return new XamlTriageService(NullLogger<XamlTriageService>.Instance, dirService, nuget);
    }

    private ResolvedTriageBinaries FakeBinaries(bool hasSymSrv, string source = "unit-source")
    {
        var binDir = Path.Combine(_tempRoot, "bin");
        Directory.CreateDirectory(binDir);
        var jsProvider = Path.Combine(binDir, "JsProvider.dll");
        File.WriteAllText(jsProvider, "stub");
        return new ResolvedTriageBinaries(binDir, jsProvider, hasSymSrv, source);
    }

    /// <summary>Builds a redirected child-process start info that runs a generated .cmd script.</summary>
    private ProcessStartInfo BatchStartInfo(string batchBody)
    {
        var path = Path.Combine(_tempRoot, $"child-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(path, batchBody);
        var psi = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add(path);
        return psi;
    }

    private static byte[] AnyExtensionBytes() => System.Text.Encoding.UTF8.GetBytes("// stub extension");

    // ---- TryAnalyzeAsync orchestration ----

    [TestMethod]
    public async Task TryAnalyzeAsync_BinariesUnavailableWithOverrideSet_ReturnsSkippedOverrideGap()
    {
        // An override directory that exists but lacks dbgeng.dll/JsProvider.dll makes IsEnvOverrideSet
        // true (so the acquire branch is skipped) and DescribeOverrideGap non-null.
        var overrideDir = Path.Combine(_tempRoot, "override-empty");
        Directory.CreateDirectory(overrideDir);
        Environment.SetEnvironmentVariable(XamlTriageBinaries.EnvOverride, overrideDir);

        var service = CreateService();
        service.BinariesResolverOverride = _ => null;

        var result = await service.TryAnalyzeAsync(@"C:\does-not-matter.dmp", useSymbols: false);

        Assert.AreEqual(XamlTriageOutcome.Skipped, result.Outcome);
        Assert.IsNotNull(result.LogText);
        StringAssert.Contains(result.LogText, "authoritative");
        StringAssert.Contains(result.LogText, "missing dbgeng.dll");
    }

    [TestMethod]
    public async Task TryAnalyzeAsync_NoOverride_RunsDownloadOnFirstUseAcquireThenSkips()
    {
        // With no WINAPP_DBGTOOLS_DIR override (cleared in Setup) and no resolvable layout, the
        // orchestration enters the download-on-first-use acquire branch. Seed the fake NuGet global
        // cache with the engine components so TryAcquireFromNuGetAsync copies them locally (offline),
        // which makes HasEngine(cacheBinDir) true and drives the JsProvider acquisition call on
        // line 78. Neutralize that call deterministically by reporting an unsupported host
        // architecture, so it returns without any network I/O. The resolver override still reports
        // "no usable layout", so the run ends on the binaries-unavailable Skipped path.
        var service = CreateService();
        service.BinariesResolverOverride = _ => null;

        var packagesRoot = Path.Combine(_tempRoot, "nuget", "packages");
        SeedNuGetComponent(packagesRoot, "Microsoft.Debugging.Platform.DbgEng",
            ["dbgeng.dll", "dbghelp.dll", "dbgcore.dll", "dbgmodel.dll", "msdia140.dll"]);
        SeedNuGetComponent(packagesRoot, "Microsoft.Debugging.Platform.SymSrv", ["symsrv.dll"]);

        var savedArch = WinDbgJsProviderAcquirer.HostArchitectureProvider;
        WinDbgJsProviderAcquirer.HostArchitectureProvider = () => System.Runtime.InteropServices.Architecture.Wasm;
        XamlTriageResult result;
        try
        {
            result = await service.TryAnalyzeAsync(@"C:\crash.dmp", useSymbols: false);
        }
        finally
        {
            WinDbgJsProviderAcquirer.HostArchitectureProvider = savedArch;
        }

        Assert.AreEqual(XamlTriageOutcome.Skipped, result.Outcome);
        Assert.IsNotNull(result.LogText);
        StringAssert.Contains(result.LogText, "could not be obtained");

        // The engine bits were copied out of the seeded global cache into the first-use cache,
        // proving the NuGet acquisition ran and that HasEngine gated the JsProvider acquisition call.
        var cacheBinDir = Path.Combine(_tempRoot, "dbgtools", XamlTriageBinaries.KitsArch);
        Assert.IsTrue(File.Exists(Path.Combine(cacheBinDir, "dbgeng.dll")),
            "dbgeng.dll should have been copied from the seeded NuGet global cache.");
        Assert.IsTrue(XamlTriageBinaries.HasEngine(new DirectoryInfo(cacheBinDir)));
    }

    private static void SeedNuGetComponent(string packagesRoot, string package, string[] files)
    {
        var archDir = Path.Combine(
            packagesRoot, package.ToLowerInvariant(), XamlTriageBinaries.DbgPackageVersion, "content", XamlTriageBinaries.NuGetArch);
        Directory.CreateDirectory(archDir);
        foreach (var file in files)
        {
            File.WriteAllText(Path.Combine(archDir, file), $"{file}-content");
        }
    }

    [TestMethod]
    public async Task TryAnalyzeAsync_SuccessNoSymbols_ReturnsSucceededWithVerdict()
    {
        var service = CreateService();
        service.BinariesResolverOverride = _ => FakeBinaries(hasSymSrv: true);
        service.ExtensionBytesDownloader = (_, _) => Task.FromResult(AnyExtensionBytes());
        service.ExtensionHashValidatorOverride = _ => true;
        service.TriageStartInfoFactory = (_, _, _, _) => BatchStartInfo(
            "@echo off\r\n" +
            "echo Error Code: 0x80004005\r\n" +
            "echo Error Message: The parameter is incorrect.\r\n" +
            "exit /b 0\r\n");

        var result = await service.TryAnalyzeAsync(@"C:\crash.dmp", useSymbols: false);

        Assert.AreEqual(XamlTriageOutcome.Succeeded, result.Outcome);
        Assert.IsNotNull(result.LogText);
        StringAssert.Contains(result.LogText, "source: unit-source");
        StringAssert.Contains(result.LogText, "Error Code: 0x80004005");
        Assert.AreEqual("0x80004005 — The parameter is incorrect.", result.Verdict);

        // The accepted extension bytes are cached to disk on success.
        Assert.IsTrue(File.Exists(Path.Combine(_tempRoot, "dbgtools", "ext", "winui-dbgext.js")));
    }

    [TestMethod]
    public async Task TryAnalyzeAsync_SymbolsRequestedButNoSymSrv_IncludesSymbolNote()
    {
        var service = CreateService();
        service.BinariesResolverOverride = _ => FakeBinaries(hasSymSrv: false, source: "no-symsrv");
        service.ExtensionBytesDownloader = (_, _) => Task.FromResult(AnyExtensionBytes());
        service.ExtensionHashValidatorOverride = _ => true;
        service.TriageStartInfoFactory = (_, _, _, _) => BatchStartInfo(
            "@echo off\r\necho Stowed exception breakdown\r\nexit /b 0\r\n");

        var result = await service.TryAnalyzeAsync(@"C:\crash.dmp", useSymbols: true);

        Assert.AreEqual(XamlTriageOutcome.Succeeded, result.Outcome);
        Assert.IsNotNull(result.LogText);
        StringAssert.Contains(result.LogText, "symsrv.dll was not found");
    }

    [TestMethod]
    public async Task TryAnalyzeAsync_ExtensionHashMismatch_ReturnsSkipped()
    {
        var service = CreateService();
        service.BinariesResolverOverride = _ => FakeBinaries(hasSymSrv: true);
        service.ExtensionBytesDownloader = (_, _) => Task.FromResult(AnyExtensionBytes());
        service.ExtensionHashValidatorOverride = _ => false; // integrity gate rejects it

        var result = await service.TryAnalyzeAsync(@"C:\crash.dmp", useSymbols: false);

        Assert.AreEqual(XamlTriageOutcome.Skipped, result.Outcome);
        Assert.IsNotNull(result.LogText);
        StringAssert.Contains(result.LogText, "winui-dbgext.js");
    }

    [TestMethod]
    public async Task TryAnalyzeAsync_ChildProducesNoOutput_ReturnsSkippedNoOutput()
    {
        var service = CreateService();
        service.BinariesResolverOverride = _ => FakeBinaries(hasSymSrv: true);
        service.ExtensionBytesDownloader = (_, _) => Task.FromResult(AnyExtensionBytes());
        service.ExtensionHashValidatorOverride = _ => true;
        service.TriageStartInfoFactory = (_, _, _, _) => BatchStartInfo("@echo off\r\nexit /b 0\r\n");

        var result = await service.TryAnalyzeAsync(@"C:\crash.dmp", useSymbols: false);

        Assert.AreEqual(XamlTriageOutcome.Skipped, result.Outcome);
        Assert.IsNotNull(result.LogText);
        StringAssert.Contains(result.LogText, "no output");
    }

    [TestMethod]
    public async Task TryAnalyzeAsync_ChildExitsNonZero_ReturnsSkippedWithExitCode()
    {
        var service = CreateService();
        service.BinariesResolverOverride = _ => FakeBinaries(hasSymSrv: true);
        service.ExtensionBytesDownloader = (_, _) => Task.FromResult(AnyExtensionBytes());
        service.ExtensionHashValidatorOverride = _ => true;
        service.TriageStartInfoFactory = (_, _, _, _) => BatchStartInfo(
            "@echo off\r\necho boom 1>&2\r\nexit /b 5\r\n");

        var result = await service.TryAnalyzeAsync(@"C:\crash.dmp", useSymbols: false);

        Assert.AreEqual(XamlTriageOutcome.Skipped, result.Outcome);
        Assert.IsNotNull(result.LogText);
        StringAssert.Contains(result.LogText, "exited with code 5");
    }

    [TestMethod]
    public async Task TryAnalyzeAsync_CallerCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var service = CreateService();
        service.BinariesResolverOverride = _ => FakeBinaries(hasSymSrv: true);
        service.ExtensionBytesDownloader = (_, ct) => throw new OperationCanceledException(ct);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => service.TryAnalyzeAsync(@"C:\crash.dmp", useSymbols: false, cts.Token));
    }

    [TestMethod]
    public async Task TryAnalyzeAsync_InternalDownloadTimeout_FailsOpenReturnsNone()
    {
        var service = CreateService();
        service.BinariesResolverOverride = _ => FakeBinaries(hasSymSrv: true);
        // Simulate HttpClient.Timeout: an OCE whose token is unrelated to the (never-cancelled) caller.
        service.ExtensionBytesDownloader = (_, _) =>
            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");

        var result = await service.TryAnalyzeAsync(@"C:\crash.dmp", useSymbols: false, CancellationToken.None);

        Assert.AreEqual(XamlTriageOutcome.None, result.Outcome);
    }

    [TestMethod]
    public async Task TryAnalyzeAsync_ResolverThrows_FailsOpenReturnsNone()
    {
        var service = CreateService();
        service.BinariesResolverOverride = _ => throw new InvalidOperationException("resolver blew up");

        var result = await service.TryAnalyzeAsync(@"C:\crash.dmp", useSymbols: false);

        Assert.AreEqual(XamlTriageOutcome.None, result.Outcome);
    }

    [TestMethod]
    public async Task EnsureExtensionAsync_CachedExtensionMatchesHash_ReturnsPathWithoutDownloading()
    {
        // First call downloads + writes the extension; the second must satisfy from the cached file
        // (File.Exists && hash matches) and therefore never invoke the downloader again.
        var service = CreateService();
        var root = new DirectoryInfo(Path.Combine(_tempRoot, "ext-cachehit"));
        service.ExtensionHashValidatorOverride = _ => true;
        service.ExtensionBytesDownloader = (_, _) => Task.FromResult(AnyExtensionBytes());

        var first = await service.EnsureExtensionAsync(root, CancellationToken.None);
        Assert.IsNotNull(first, "The first call should download and cache the extension.");
        Assert.IsTrue(File.Exists(first), "The extension file should have been written to the cache.");

        service.ExtensionBytesDownloader = (_, _) => throw new InvalidOperationException("cache hit must not download");
        var second = await service.EnsureExtensionAsync(root, CancellationToken.None);

        Assert.AreEqual(first, second, "A cached extension matching the pinned hash must be reused without downloading.");
    }

    [TestMethod]
    public async Task EnsureExtensionAsync_DownloadThrowsNonCancellation_ReturnsNull()
    {
        // A transport-level failure (not cancellation) while fetching the extension must be swallowed
        // and reported as "no extension" so triage fails open rather than crashing.
        var service = CreateService();
        var root = new DirectoryInfo(Path.Combine(_tempRoot, "ext-download-throws"));
        service.ExtensionHashValidatorOverride = _ => true;
        service.ExtensionBytesDownloader = (_, _) => throw new HttpRequestException("network down");

        var result = await service.EnsureExtensionAsync(root, CancellationToken.None);

        Assert.IsNull(result, "A failed extension download must return null (extension unavailable).");
    }

    [TestMethod]
    public async Task TryAnalyzeAsync_NuGetCacheDirUnresolvable_FailsOpenAndSkips()
    {
        // The NuGet service can't resolve its global packages directory (throws). The acquire branch
        // must tolerate that (TryGetNuGetCacheDir returns null) and continue offline. The first-use
        // cache is pre-seeded with usable engine bits so acquisition short-circuits without any
        // network, and an unsupported host arch neutralizes the JsProvider acquisition. With no
        // resolvable layout, the run ends on the binaries-unavailable Skipped path.
        var dirService = new FakeWinappDirectoryService(new DirectoryInfo(_tempRoot));
        var nuget = new FakeNugetService { CacheDirectory = null };
        var service = new XamlTriageService(NullLogger<XamlTriageService>.Instance, dirService, nuget);
        service.BinariesResolverOverride = _ => null;

        var cacheBinDir = Path.Combine(_tempRoot, "dbgtools", XamlTriageBinaries.KitsArch);
        Directory.CreateDirectory(cacheBinDir);
        foreach (var file in new[] { "dbgeng.dll", "dbghelp.dll", "dbgcore.dll", "dbgmodel.dll", "msdia140.dll", "symsrv.dll" })
        {
            WriteFakePe(Path.Combine(cacheBinDir, file));
        }

        var savedArch = WinDbgJsProviderAcquirer.HostArchitectureProvider;
        WinDbgJsProviderAcquirer.HostArchitectureProvider = () => System.Runtime.InteropServices.Architecture.Wasm;
        XamlTriageResult result;
        try
        {
            result = await service.TryAnalyzeAsync(@"C:\crash.dmp", useSymbols: false);
        }
        finally
        {
            WinDbgJsProviderAcquirer.HostArchitectureProvider = savedArch;
        }

        Assert.AreEqual(XamlTriageOutcome.Skipped, result.Outcome,
            "An unresolvable NuGet cache must not abort triage; it fails open to Skipped.");
    }

    private static void WriteFakePe(string path)
    {
        var bytes = new byte[8192];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        File.WriteAllBytes(path, bytes);
    }

    // ---- RunTriageProcessAsync direct ----

    [TestMethod]
    public async Task RunTriageProcessAsync_Timeout_ReturnsTimedOutNote()
    {
        XamlTriageService.TriageTimeoutOverride = TimeSpan.FromMilliseconds(50);
        var service = CreateService();
        service.TriageStartInfoFactory = (_, _, _, _) => BatchStartInfo(
            "@echo off\r\nping -n 6 127.0.0.1 >nul\r\nexit /b 0\r\n");

        var (output, skipNote) = await service.RunTriageProcessAsync(
            @"C:\crash.dmp", FakeBinaries(hasSymSrv: true), @"C:\ext.js", useSymbols: false, CancellationToken.None);

        Assert.IsNull(output);
        Assert.IsNotNull(skipNote);
        StringAssert.Contains(skipNote, "timed out");
    }

    [TestMethod]
    public async Task RunTriageProcessAsync_StatusBreakpointExit_ReturnsBreakpointNote()
    {
        var service = CreateService();
        // -2147483645 == unchecked((int)0x80000003) == STATUS_BREAKPOINT.
        service.TriageStartInfoFactory = (_, _, _, _) =>
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add("[Environment]::Exit(-2147483645)");
            return psi;
        };

        var (output, skipNote) = await service.RunTriageProcessAsync(
            @"C:\crash.dmp", FakeBinaries(hasSymSrv: true), @"C:\ext.js", useSymbols: false, CancellationToken.None);

        Assert.IsNull(output);
        Assert.IsNotNull(skipNote);
        StringAssert.Contains(skipNote, "STATUS_BREAKPOINT");
    }

    [TestMethod]
    public async Task RunTriageProcessAsync_ExtendedCrash_RetriesStowedOnlyOnce()
    {
        var service = CreateService();
        var invocations = 0;
        service.TriageStartInfoFactory = (_, _, _, _) =>
        {
            invocations++;
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(invocations == 1
                ? "[Environment]::Exit(-1073740791)"
                : "Write-Output 'STOWED-DETAIL'");
            return psi;
        };

        var (output, skipNote) = await service.RunTriageProcessAsync(
            @"C:\crash.dmp",
            FakeBinaries(hasSymSrv: true),
            @"C:\ext.js",
            useSymbols: false,
            CancellationToken.None);

        Assert.AreEqual(2, invocations);
        Assert.IsNull(skipNote);
        StringAssert.Contains(output, "STATUS_STACK_BUFFER_OVERRUN");
        StringAssert.Contains(output, "STOWED-DETAIL");
    }

    [TestMethod]
    public async Task RunTriageProcessAsync_GenuineCancellation_Throws()
    {
        using var cts = new CancellationTokenSource();
        var service = CreateService();
        service.TriageStartInfoFactory = (_, _, _, _) => BatchStartInfo(
            "@echo off\r\nping -n 10 127.0.0.1 >nul\r\nexit /b 0\r\n");

        cts.CancelAfter(TimeSpan.FromMilliseconds(200));

        // WaitForExitAsync surfaces a TaskCanceledException (an OperationCanceledException subclass),
        // which RunTriageProcessAsync rethrows after killing the child. Use the assignable-type assert.
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.RunTriageProcessAsync(
            @"C:\crash.dmp", FakeBinaries(hasSymSrv: true), @"C:\ext.js", useSymbols: false, cts.Token));
    }

    // ---- BuildTriageStartInfo / UnavailableNote ----

    [TestMethod]
    public void BuildTriageStartInfo_ReinvokesCurrentBinaryWithTriageArgs()
    {
        var service = CreateService();
        var binaries = FakeBinaries(hasSymSrv: true);

        var startInfo = service.BuildTriageStartInfo(@"C:\crash.dmp", binaries, @"C:\ext.js", useSymbols: true);

        Assert.IsFalse(string.IsNullOrEmpty(startInfo.FileName));
        Assert.IsTrue(startInfo.RedirectStandardOutput);
        Assert.IsTrue(startInfo.RedirectStandardError);
        CollectionAssert.Contains(startInfo.ArgumentList.ToList(), XamlTriageRunner.InternalVerb);
        CollectionAssert.Contains(startInfo.ArgumentList.ToList(), @"C:\crash.dmp");
    }

    [TestMethod]
    public void BuildTriageStartInfo_UnderDotnetHost_PassesManagedEntryAssemblyAsFirstArg()
    {
        // The MTP test host is an apphost (never dotnet.exe), so the dev/test dotnet-host re-invocation
        // branch is only reachable by pointing the process-path seam at a "dotnet"-named binary.
        var original = XamlTriageService.ProcessPathProvider;
        try
        {
            XamlTriageService.ProcessPathProvider = () => @"C:\Program Files\dotnet\dotnet.exe";
            var service = CreateService();
            var binaries = FakeBinaries(hasSymSrv: true);

            var startInfo = service.BuildTriageStartInfo(@"C:\crash.dmp", binaries, @"C:\ext.js", useSymbols: false);

            Assert.AreEqual(@"C:\Program Files\dotnet\dotnet.exe", startInfo.FileName);
            // Under the dotnet host the managed entry assembly must be the FIRST argument, ahead of the verb.
            Assert.IsTrue(startInfo.ArgumentList.Count > 0);
            StringAssert.EndsWith(startInfo.ArgumentList[0], ".dll");
            StringAssert.StartsWith(startInfo.ArgumentList[0], AppContext.BaseDirectory);
            Assert.AreEqual(XamlTriageRunner.InternalVerb, startInfo.ArgumentList[1]);
            CollectionAssert.Contains(startInfo.ArgumentList.ToList(), @"C:\crash.dmp");
        }
        finally
        {
            XamlTriageService.ProcessPathProvider = original;
        }
    }

    [TestMethod]
    public void UnavailableNote_NoOverride_ReturnsDefaultGuidance()
    {
        Environment.SetEnvironmentVariable(XamlTriageBinaries.EnvOverride, null);

        var note = XamlTriageService.UnavailableNote();

        StringAssert.Contains(note, "could not be obtained");
        StringAssert.Contains(note, "Debugging Tools for Windows");
        // Without an override, DescribeOverrideGap is null, so the "authoritative" branch is not taken.
        Assert.IsFalse(note.Contains("authoritative", StringComparison.Ordinal));
    }

    [TestMethod]
    public void UnavailableNote_IncompleteOverride_ReturnsOverrideGapGuidance()
    {
        var overrideDir = Path.Combine(_tempRoot, "override-gap");
        Directory.CreateDirectory(overrideDir);
        Environment.SetEnvironmentVariable(XamlTriageBinaries.EnvOverride, overrideDir);

        var note = XamlTriageService.UnavailableNote();

        StringAssert.Contains(note, "authoritative");
        StringAssert.Contains(note, "missing dbgeng.dll");
    }
}
