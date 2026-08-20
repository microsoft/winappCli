// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace WinApp.Cli.Services;

/// <summary>
/// Runs the WinUI team's WinDbg JavaScript extension against a minidump by hosting DbgEng
/// directly, producing the stowed-exception breakdown and XAML dispatch triage that the
/// standard ClrMD/DbgEng passes cannot recover.
/// </summary>
internal sealed partial class XamlTriageService(
    ILogger<XamlTriageService> logger,
    IWinappDirectoryService winappDirectoryService,
    INugetService nugetService) : IXamlTriageService
{
    // Pinned WinUI debugger extension (microsoft/microsoft-ui-xaml). See plan / docs for rationale.
    private const string ExtCommit = "29d537445eaa34d47e66ab8859583ae953c62dd1";
    private const string ExtRepoPath = "dbgext/publicXamlThread/winui-dbgext.js";
    private const string ExtBlobSha1 = "820f8f7d45dc3df82623ac5163dcea8d8212e2d2";
    private const string ExtFileName = "winui-dbgext.js";

    // Hard ceiling for the isolated triage process (symbol downloads can be slow on first run).
    private static readonly TimeSpan TriageTimeout = TimeSpan.FromMinutes(5);
    private const int StatusStackBufferOverrun = unchecked((int)0xC0000409);

    // For testing only — overrides the child-process timeout so the timeout branch can be exercised
    // deterministically without a 5-minute wait. Null means use the real ceiling.
    internal static TimeSpan? TriageTimeoutOverride { get; set; }

    // For testing only — the GitHub download boundary for the debugger extension. Default performs the
    // real HTTPS GET; tests supply canned bytes (or throw) to exercise EnsureExtensionAsync hermetically.
    internal Func<string, CancellationToken, Task<byte[]>>? ExtensionBytesDownloader { get; set; }

    // For testing only — the extension integrity gate. Null means use the real pinned-hash check; tests
    // can accept controlled bytes so the write/early-return success paths run without the real content.
    internal Func<byte[], bool>? ExtensionHashValidatorOverride { get; set; }

    // For testing only — builds the triage child ProcessStartInfo. Null means re-invoke the current
    // binary (production); tests point it at a controlled child to exercise the execution/exit branches.
    internal Func<string, ResolvedTriageBinaries, string, bool, ProcessStartInfo>? TriageStartInfoFactory { get; set; }

    // For testing only — the debugger-layout resolution boundary (Authenticode/version-gated file
    // probing). Null means use the real XamlTriageBinaries.ResolveExisting; tests inject a layout so
    // the orchestration past resolution can run without real signed binaries.
    internal Func<DirectoryInfo, ResolvedTriageBinaries?>? BinariesResolverOverride { get; set; }

    // For testing only — the current-process path OS boundary. Default returns the real
    // Environment.ProcessPath; tests point it at a "dotnet"-named path to exercise the dotnet-host
    // re-invocation branch in BuildTriageStartInfo (the test host is an apphost, never dotnet.exe).
    internal static Func<string?> ProcessPathProvider { get; set; } = () => Environment.ProcessPath;

    /// <inheritdoc/>
    public async Task<XamlTriageResult> TryAnalyzeAsync(string dumpPath, bool useSymbols, CancellationToken cancellationToken = default)
    {
        try
        {
            var dbgToolsRoot = new DirectoryInfo(Path.Combine(
                winappDirectoryService.GetGlobalWinappDirectory().FullName, "dbgtools"));
            var cacheBinDir = new DirectoryInfo(Path.Combine(dbgToolsRoot.FullName, XamlTriageBinaries.KitsArch));

            ResolvedTriageBinaries? ResolveExisting(DirectoryInfo dir) =>
                (BinariesResolverOverride ?? (d => XamlTriageBinaries.ResolveExisting(d, logger)))(dir);

            // Resolve an existing debugger layout; if none, populate the download-on-first-use cache:
            // engine bits from NuGet (global cache or download) and JsProvider.dll from the WinDbg bundle.
            var binaries = ResolveExisting(cacheBinDir);
            if (binaries == null && !XamlTriageBinaries.IsEnvOverrideSet)
            {
                // Only populate the download-on-first-use cache when no authoritative override is set;
                // with an override configured, ResolveExisting never consults the cache, so acquiring
                // into it would waste the download and still report triage as unavailable.
                var nugetCacheDir = TryGetNuGetCacheDir();
                await XamlTriageBinaries.TryAcquireFromNuGetAsync(cacheBinDir, nugetCacheDir, logger, cancellationToken);

                // JsProvider.dll only ships in the WinDbg bundle; acquire it once the engine is present.
                if (XamlTriageBinaries.HasEngine(cacheBinDir))
                {
                    await WinDbgJsProviderAcquirer.TryAcquireAsync(cacheBinDir, logger, cancellationToken);
                }

                binaries = ResolveExisting(cacheBinDir);
            }

            if (binaries == null)
            {
                logger.LogDebug("WinUI triage skipped: debugging binaries (incl. JsProvider.dll) unavailable.");
                return XamlTriageResult.Skipped(UnavailableNote());
            }

            var extPath = await EnsureExtensionAsync(dbgToolsRoot, cancellationToken);
            if (extPath == null)
            {
                logger.LogDebug("WinUI triage skipped: could not obtain {Ext}.", ExtFileName);
                return XamlTriageResult.Skipped(
                    $"WinUI Triage: skipped — the WinUI debugger extension ({ExtFileName}) could not be " +
                    "obtained (download blocked or its pinned hash did not match).");
            }

            // Run the DbgEng pass in a dedicated child process. The parent has already loaded the
            // system32 dbghelp.dll (dump capture + ClrMD analysis), which prevents the modern NuGet
            // dbgeng.dll from binding to its co-located dbghelp.dll. A clean process avoids that.
            var (output, skipNote) = await RunTriageProcessAsync(dumpPath, binaries, extPath, useSymbols, cancellationToken);
            if (skipNote != null)
            {
                return XamlTriageResult.Skipped($"WinUI Triage: skipped — {skipNote}");
            }

            if (string.IsNullOrWhiteSpace(output))
            {
                return XamlTriageResult.Skipped("WinUI Triage: skipped — the triage pass produced no output.");
            }

            var header = $"WinUI Triage (DbgEng + winui-dbgext.js, source: {binaries.Source}):";

            // The user asked for symbols but the resolved engine layout has no symsrv.dll, so the
            // child ran without symbol downloads; explain why the breakdown may be incomplete.
            var symbolNote = useSymbols && !binaries.HasSymSrv
                ? "Note: --symbols was requested but symsrv.dll was not found alongside the debugging " +
                  $"engine ({binaries.Source}), so symbols could not be downloaded. Install Debugging " +
                  $"Tools for Windows or point {XamlTriageBinaries.EnvOverride} at a layout that includes symsrv.dll."
                : null;

            var gapNote = DescribeSymbolGap(output, useSymbols);
            var notes = string.Join("\n", new[] { symbolNote, gapNote }.Where(n => n != null));
            var logText = notes.Length == 0
                ? $"{header}\n{output.Trim()}"
                : $"{header}\n{notes}\n\n{output.Trim()}";
            return XamlTriageResult.Succeeded(logText, TryExtractVerdict(output));
        }
        catch (OperationCanceledException ex) when (ShouldPropagateCancellation(ex, cancellationToken))
        {
            // Genuine caller cancellation propagates; internal HttpClient.Timeout cancellations fall
            // through to the fail-open handler below so the already-computed managed stack survives.
            throw;
        }
        catch (OperationCanceledException)
        {
            // Internal download timeout (not caller-requested). Fail open without dumping the raw
            // TaskCanceledException stack to the console — the caller keeps the managed crash stack.
            logger.LogDebug("WinUI triage pass timed out acquiring debugging tools; skipping triage.");
            return XamlTriageResult.None;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "WinUI triage pass failed.");
            return XamlTriageResult.None;
        }
    }

    /// <summary>
    /// Decides whether an <see cref="OperationCanceledException"/> from the triage pipeline represents
    /// genuine caller cancellation (rethrow) versus an internal <see cref="HttpClient"/> timeout
    /// (swallow, so the already-computed managed crash stack is preserved by the caller). Only the
    /// caller's own token being cancelled counts as genuine cancellation — internal download timeouts
    /// surface as <see cref="TaskCanceledException"/> with an unrelated token and must not propagate.
    /// </summary>
    internal static bool ShouldPropagateCancellation(OperationCanceledException ex, CancellationToken callerToken)
    {
        _ = ex;
        return callerToken.IsCancellationRequested;
    }

    /// <summary>
    /// Best-effort extraction of a concise one-line verdict (error code and/or message) from the raw
    /// extension output, so the console can show the headline finding instead of only pointing at the
    /// log. Returns <c>null</c> when no recognizable error code/message line is present.
    /// </summary>
    internal static string? TryExtractVerdict(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        string? code = null;
        string? message = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (code == null)
            {
                var m = ErrorCodeRegex().Match(line);
                if (m.Success)
                {
                    code = m.Groups[1].Value.Trim();
                }
                else
                {
                    m = RawHresultRegex().Match(line);
                    if (m.Success)
                    {
                        code = "0x" + m.Groups[1].Value.ToUpperInvariant();
                    }
                }
            }

            if (message == null)
            {
                var m = ErrorMessageRegex().Match(line);
                if (m.Success && m.Groups[1].Value.Trim().Length > 0)
                {
                    message = m.Groups[1].Value.Trim();
                }
            }

            if (code != null && message != null)
            {
                break;
            }
        }

        if (code == null && message == null)
        {
            return null;
        }

        var classification =
            output.Contains("GetResourcePropertyBag", StringComparison.OrdinalIgnoreCase)
            && output.Contains("GetXamlProperty", StringComparison.OrdinalIgnoreCase)
                ? "XAML resource property resolution failed; inspect x:Uid/.resw property keys."
                : null;
        return string.Join(
            " — ",
            new[] { code, message, classification }.Where(part => !string.IsNullOrEmpty(part)));
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(?:error\s*code|hresult)\s*[:=]\s*(0x[0-9a-fA-F]+)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex ErrorCodeRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"error\s*(?:message|text)\s*[:=]\s*(.+)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex ErrorMessageRegex();

    [System.Text.RegularExpressions.GeneratedRegex(
        @"\bhr\s*=\s*(?:0x)?([0-9a-fA-F]{8})\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex RawHresultRegex();

    /// <summary>
    /// Detects the "operating-system symbols unavailable" failure shape — the extension identifies the
    /// stowed exception but can't expand its <c>combase.dll</c> structures without OS symbols — and
    /// returns a clear explanation to prepend, so the log doesn't end on a cryptic internal JS error.
    /// Returns <c>null</c> when the output doesn't show that signature.
    /// </summary>
    internal static string? DescribeSymbolGap(string output, bool useSymbols)
    {
        var symbolsMissing = output.Contains("combase.dll not loaded/unavailable", StringComparison.OrdinalIgnoreCase)
            || (output.Contains("Symbol Loading Error Summary", StringComparison.OrdinalIgnoreCase)
                && output.Contains("combase", StringComparison.OrdinalIgnoreCase));
        var decodeFailed = output.Contains("createPointerObject", StringComparison.OrdinalIgnoreCase);

        if (!symbolsMissing || !decodeFailed)
        {
            return null;
        }

        var remedy = useSymbols
            ? "The public Microsoft symbol server did not have combase.dll symbols for this Windows build " +
              "(it returned 404). Run the same analysis on a build whose OS symbols are published, or point " +
              "the engine at a local symbol store that contains them, to get the full breakdown."
            : "Re-run with --symbols so the operating-system symbols for combase.dll can be downloaded, then " +
              "the stowed exception can be fully expanded.";

        return "Note: a stowed exception (0xC000027B) was detected but could not be fully expanded because " +
            "operating-system symbols for combase.dll were unavailable. " + remedy +
            "\n(The raw extension output below is kept for reference.)";
    }

    /// <summary>
    /// Builds the argument list for the hidden <c>__xaml-triage</c> child verb. Extracted for
    /// testability: <c>--symbols</c> is only forwarded when the user asked for symbols <em>and</em>
    /// the resolved layout actually has <c>symsrv.dll</c>, and the resolved <c>JsProvider.dll</c>
    /// path (which may be in a <c>winext</c> subfolder) is passed explicitly.
    /// </summary>
    internal static List<string> BuildTriageArgs(string dumpPath, ResolvedTriageBinaries binaries, string extPath, bool useSymbols)
    {
        var args = new List<string>
        {
            XamlTriageRunner.InternalVerb,
            "--dump", dumpPath,
            "--bin", binaries.BinDir,
            "--jsprovider", binaries.JsProviderPath,
            "--ext", extPath,
        };
        if (useSymbols && binaries.HasSymSrv)
        {
            args.Add("--symbols");
        }

        return args;
    }

    /// <summary>
    /// Spawns the hidden <c>__xaml-triage</c> verb in a fresh process and captures its stdout. The
    /// isolation is essential: see <see cref="XamlTriageRunner"/> for the dbghelp.dll loader-collision
    /// rationale. Works both as a published single-file executable and under <c>dotnet winapp.dll</c>.
    /// Returns the captured output, or a short human-readable skip note describing why no output is
    /// available (failed to start, timed out, or non-zero exit).
    /// </summary>
    internal async Task<(string? Output, string? SkipNote)> RunTriageProcessAsync(
        string dumpPath,
        ResolvedTriageBinaries binaries,
        string extPath,
        bool useSymbols,
        CancellationToken cancellationToken,
        bool stowedOnly = false)
    {
        var startInfo = (TriageStartInfoFactory ?? BuildTriageStartInfo)(dumpPath, binaries, extPath, useSymbols);
        if (stowedOnly)
        {
            startInfo.ArgumentList.Add("--stowed-only");
        }

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) { stdout.AppendLine(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) { stderr.AppendLine(e.Data); } };

        if (!process.Start())
        {
            logger.LogDebug("WinUI triage child process failed to start.");
            return (null, "the triage child process could not be started.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var timeout = TriageTimeoutOverride ?? TriageTimeout;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            // WaitForExitAsync returns once the process exits, but the async stdout/stderr readers may
            // still have buffered data in flight. The parameterless overload blocks until those readers
            // have flushed, so the StringBuilders are complete before we read them.
            process.WaitForExit();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            logger.LogDebug("WinUI triage child process timed out after {Timeout}.", timeout);
            return (null, $"the triage child process timed out after {timeout.TotalMinutes:0} minutes.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        if (process.ExitCode != 0)
        {
            logger.LogDebug("WinUI triage child process exited with code {Code}: {Error}",
                process.ExitCode, stderr.ToString().Trim());

            // STATUS_BREAKPOINT is the signature of loading a JsProvider.dll built against a different
            // engine version than the pinned dbgeng.dll — the version-compat gate should prevent this,
            // but surface a clearer verdict than a raw negative exit code if it ever slips through.
            if (process.ExitCode == unchecked((int)0x80000003))
            {
                return (null, "the triage child process crashed on startup (STATUS_BREAKPOINT), which usually means the JsProvider.dll build does not match the debugging engine.");
            }

            if (process.ExitCode == StatusStackBufferOverrun
                && !stowedOnly)
            {
                logger.LogDebug(
                    "Extended WinUI triage crashed with STATUS_STACK_BUFFER_OVERRUN; retrying the bounded stowed-exception-only pass.");
                startInfo.ArgumentList.Add("--stowed-only");
                var (fallbackOutput, fallbackSkipNote) = await RunTriageProcessAsync(
                    dumpPath,
                    binaries,
                    extPath,
                    useSymbols,
                    cancellationToken,
                    stowedOnly: true);
                if (fallbackSkipNote is null && !string.IsNullOrWhiteSpace(fallbackOutput))
                {
                    return (
                        "Extended !xamltriage crashed with STATUS_STACK_BUFFER_OVERRUN; " +
                        "recovered the deterministic !xamlstowed output:\n" +
                        fallbackOutput,
                        null);
                }
                return (null, fallbackSkipNote
                    ?? "the extended triage pass crashed with STATUS_STACK_BUFFER_OVERRUN and the stowed-exception fallback produced no output.");
            }

            return (null, $"the triage child process exited with code {process.ExitCode}.");
        }

        return (stdout.ToString(), null);
    }

    /// <summary>
    /// Builds the <see cref="ProcessStartInfo"/> that re-invokes the current binary's hidden
    /// <c>__xaml-triage</c> verb. When running under the dotnet host (dev/test) the managed entry
    /// assembly is passed as the first argument. Extracted so it is unit-testable without spawning.
    /// </summary>
    internal ProcessStartInfo BuildTriageStartInfo(string dumpPath, ResolvedTriageBinaries binaries, string extPath, bool useSymbols)
    {
        var startInfo = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Re-invoke the current binary. When running under the dotnet host (dev/test), ProcessPath is
        // dotnet.exe and we must pass the managed entry assembly as the first argument.
        var processPath = ProcessPathProvider()!;
        startInfo.FileName = processPath;
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            // Only reached under the dotnet host (dev/test). Derive the managed entry DLL path from
            // the app base directory + assembly simple name rather than Assembly.Location, which is
            // empty for single-file apps and trips the IL3000 single-file/AOT analyzer.
            var entryName = Assembly.GetEntryAssembly()!.GetName().Name;
            startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, entryName + ".dll"));
        }

        foreach (var arg in BuildTriageArgs(dumpPath, binaries, extPath, useSymbols))
        {
            startInfo.ArgumentList.Add(arg);
        }

        return startInfo;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>
    /// Ensures the pinned <c>winui-dbgext.js</c> is present in the cache and matches its pinned
    /// git blob hash, downloading it on first use. Returns the local path or <c>null</c> on failure.
    /// </summary>
    internal async Task<string?> EnsureExtensionAsync(DirectoryInfo dbgToolsRoot, CancellationToken cancellationToken)
    {
        var extDir = Path.Combine(dbgToolsRoot.FullName, "ext");
        Directory.CreateDirectory(extDir);
        var extPath = Path.Combine(extDir, ExtFileName);

        bool MatchesHash(byte[] content) => (ExtensionHashValidatorOverride ?? MatchesPinnedExtensionHash)(content);

        if (File.Exists(extPath) && MatchesHash(await File.ReadAllBytesAsync(extPath, cancellationToken)))
        {
            return extPath;
        }

        try
        {
            var url = $"https://raw.githubusercontent.com/microsoft/microsoft-ui-xaml/{ExtCommit}/{ExtRepoPath}";
            var bytes = await (ExtensionBytesDownloader ?? DownloadExtensionBytesAsync)(url, cancellationToken);

            if (!MatchesHash(bytes))
            {
                logger.LogWarning("Downloaded {Ext} hash mismatch (expected {Expected}, got {Actual}); refusing to use it.",
                    ExtFileName, ExtBlobSha1, GitBlobSha1(bytes));
                return null;
            }

            await File.WriteAllBytesAsync(extPath, bytes, cancellationToken);
            return extPath;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Failed to download {Ext}.", ExtFileName);
            return null;
        }
    }

    /// <summary>Real GitHub download boundary for the debugger extension; seamed via <see cref="ExtensionBytesDownloader"/>.</summary>
    private static async Task<byte[]> DownloadExtensionBytesAsync(string url, CancellationToken cancellationToken)
    {
        using var http = new HttpClient();
        return await http.GetByteArrayAsync(url, cancellationToken);
    }

    /// <summary>Resolves the NuGet global packages cache directory, tolerating provider failures.</summary>
    private DirectoryInfo? TryGetNuGetCacheDir()
    {
        try
        {
            return nugetService.GetNuGetGlobalPackagesDir();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not resolve the NuGet global packages directory for triage binaries.");
            return null;
        }
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="content"/> matches the pinned <c>winui-dbgext.js</c>
    /// git blob hash. This is the integrity gate that prevents a tampered or wrong extension from
    /// being loaded into the debugger; exposed internally for testing.
    /// </summary>
    internal static bool MatchesPinnedExtensionHash(byte[] content) => GitBlobSha1(content) == ExtBlobSha1;

    /// <summary>Computes the git blob SHA-1 (<c>sha1("blob &lt;len&gt;\0" + content)</c>) of a buffer.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "SHA-1 is used only to reproduce git's content-addressed blob identity for integrity pinning, not for security.")]
    internal static string GitBlobSha1(byte[] content)
    {
        var header = Encoding.ASCII.GetBytes($"blob {content.Length}\0");
        var buffer = new byte[header.Length + content.Length];
        Buffer.BlockCopy(header, 0, buffer, 0, header.Length);
        Buffer.BlockCopy(content, 0, buffer, header.Length, content.Length);
        return Convert.ToHexStringLower(SHA1.HashData(buffer));
    }

    internal static string UnavailableNote()
    {
        // When an authoritative override is configured, the cache/installed-tools paths are skipped,
        // so telling the user to "set WINAPP_DBGTOOLS_DIR" (which is already set) is misleading.
        // Point them at the specific gap in their override directory instead.
        var overrideGap = XamlTriageBinaries.DescribeOverrideGap();
        if (overrideGap != null)
        {
            return "WinUI Triage: skipped — " + overrideGap + ".\n" +
                $"The {XamlTriageBinaries.EnvOverride} override is authoritative, so only that directory is " +
                "consulted. Populate it with a full debugger layout (dbgeng.dll + JsProvider.dll), or unset " +
                $"{XamlTriageBinaries.EnvOverride} to use installed Debugging Tools for Windows or the download-on-first-use cache.";
        }

        return "WinUI Triage: skipped — the debugger components required for stowed-exception analysis " +
            "(dbgeng.dll + JsProvider.dll) could not be obtained.\n" +
            "The engine bits come from NuGet and JsProvider.dll is extracted from the WinDbg download; " +
            "if your environment blocks those, install Debugging Tools for Windows (Windows SDK) or set " +
            $"the {XamlTriageBinaries.EnvOverride} environment variable to a debugger directory that contains them.";
    }
}
