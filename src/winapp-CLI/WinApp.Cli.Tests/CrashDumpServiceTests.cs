// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Spectre.Console.Testing;
using System.Text;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
#pragma warning disable CA1001 // Disposable fields cleaned up in TestCleanup
public class CrashDumpServiceTests
{
    private TestConsole _console = null!;
    private ILogger<CrashDumpService> _logger = null!;
    private FakeXamlTriageService _xamlTriage = null!;
    private CrashDumpService _service = null!;
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _console = new TestConsole();
        _logger = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Debug)).CreateLogger<CrashDumpService>();
        _xamlTriage = new FakeXamlTriageService();
        _service = new CrashDumpService(_console, _logger, _xamlTriage);
        _tempDir = Path.Combine(Path.GetTempPath(), $"CrashDumpTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _console?.Dispose();
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    [TestMethod]
    public async Task AnalyzeDumpAsync_NonExistentDump_ShowsFailureMessage()
    {
        // Arrange
        var dumpPath = Path.Combine(_tempDir, "nonexistent.dmp");
        var logPath = Path.Combine(_tempDir, "test.log");

        // Act
        await _service.AnalyzeDumpAsync(dumpPath, logPath);

        // Assert
        var output = _console.Output;
        Assert.IsTrue(output.Contains("Analysis failed"), $"Expected failure message in output: {output}");
        Assert.IsTrue(output.Contains("windbg"), $"Expected WinDbg fallback suggestion in output: {output}");
    }

    [TestMethod]
    public async Task AnalyzeDumpAsync_InvalidDumpFile_ShowsFailureMessage()
    {
        // Arrange — a file that exists but is not a valid dump
        var dumpPath = Path.Combine(_tempDir, "invalid.dmp");
        await File.WriteAllTextAsync(dumpPath, "this is not a valid dump file");
        var logPath = Path.Combine(_tempDir, "test.log");

        // Act
        await _service.AnalyzeDumpAsync(dumpPath, logPath);

        // Assert
        var output = _console.Output;
        Assert.IsTrue(output.Contains("Analysis failed"), $"Expected failure message in output: {output}");
        Assert.IsTrue(output.Contains("windbg"), $"Expected WinDbg fallback suggestion in output: {output}");
    }

    [TestMethod]
    public async Task AnalyzeDumpAsync_InvalidDump_WritesLogPath()
    {
        // Arrange
        var dumpPath = Path.Combine(_tempDir, "invalid.dmp");
        await File.WriteAllTextAsync(dumpPath, "not a dump");
        var logPath = Path.Combine(_tempDir, "test.log");

        // Act
        await _service.AnalyzeDumpAsync(dumpPath, logPath);

        // Assert — TestConsole wraps long paths, so check for the filename
        var output = _console.Output;
        Assert.IsTrue(output.Contains("test.log"), $"Expected log filename in output: {output}");
    }

    [TestMethod]
    public async Task AnalyzeDumpAsync_InvalidDump_ShowsDumpPath()
    {
        // Arrange
        var dumpPath = Path.Combine(_tempDir, "invalid.dmp");
        await File.WriteAllTextAsync(dumpPath, "not a dump");
        var logPath = Path.Combine(_tempDir, "test.log");

        // Act
        await _service.AnalyzeDumpAsync(dumpPath, logPath);

        // Assert — TestConsole wraps long paths, so check for the filename
        var output = _console.Output;
        Assert.IsTrue(output.Contains("invalid.dmp"), $"Expected dump filename in output: {output}");
    }

    [TestMethod]
    public async Task AnalyzeDumpAsync_InvalidDump_DoesNotRunWinUiTriage()
    {
        // Arrange — an unreadable dump can't be inspected for WinUI modules, so triage must be skipped.
        var dumpPath = Path.Combine(_tempDir, "invalid.dmp");
        await File.WriteAllTextAsync(dumpPath, "not a dump");
        var logPath = Path.Combine(_tempDir, "test.log");

        // Act
        await _service.AnalyzeDumpAsync(dumpPath, logPath);

        // Assert
        Assert.AreEqual(0, _xamlTriage.AnalyzeCalls.Count, "WinUI triage must not run for an unreadable/non-WinUI dump.");
    }

    [TestMethod]
    public async Task RunXamlTriageGuardedAsync_TriageThrows_ReturnsNoneAndDoesNotPropagate()
    {
        // Fail-open contract: even if the triage service throws (e.g. an internal HttpClient timeout
        // surfacing as OperationCanceledException on a slow first-run download), the crash-analysis flow
        // must not be derailed — otherwise the already-computed managed crash stack is discarded as
        // "Analysis failed." This guards the H1 regression.
        _xamlTriage.ThrowOnAnalyze = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");

        var result = await _service.RunXamlTriageGuardedAsync(Path.Combine(_tempDir, "any.dmp"), useSymbols: false);

        Assert.AreEqual(XamlTriageOutcome.None, result.Outcome, "A thrown triage failure must degrade to None, not propagate.");
        Assert.AreEqual(1, _xamlTriage.AnalyzeCalls.Count);
    }

    [TestMethod]
    public async Task RunXamlTriageGuardedAsync_TriageSucceeds_PassesResultThrough()
    {
        _xamlTriage.FakeResult = XamlTriageResult.Succeeded("full breakdown", "0xc000027b — boom");

        var result = await _service.RunXamlTriageGuardedAsync(Path.Combine(_tempDir, "any.dmp"), useSymbols: true);

        Assert.AreEqual(XamlTriageOutcome.Succeeded, result.Outcome);
        Assert.AreEqual("0xc000027b — boom", result.Verdict);
    }

    [TestMethod]
    public void SelectExceptionRecord_StowedWithParameters_UsesStowedRecord()
    {
        var (code, address, useStowed) = CrashDumpService.SelectExceptionRecord(
            savedExceptionCode: unchecked((int)0xC0000005), savedExceptionAddress: 0x1000,
            crashExceptionCode: unchecked((int)0xC000027B), crashExceptionAddress: 0x2000,
            crashExceptionParameters: [0xDEAD, 1]);

        Assert.IsTrue(useStowed, "A stowed exception with parameters must drive the dump's exception record.");
        Assert.AreEqual(unchecked((int)0xC000027B), code);
        Assert.AreEqual((nuint)0x2000, address);
    }

    [TestMethod]
    public void SelectExceptionRecord_StowedWithoutParameters_FallsBackToFirstChance()
    {
        var (code, address, useStowed) = CrashDumpService.SelectExceptionRecord(
            savedExceptionCode: unchecked((int)0xC0000005), savedExceptionAddress: 0x1000,
            crashExceptionCode: unchecked((int)0xC000027B), crashExceptionAddress: 0x2000,
            crashExceptionParameters: null);

        Assert.IsFalse(useStowed, "Without stowed parameters there is nothing for !xamlstowed to read, so keep the first-chance record.");
        Assert.AreEqual(unchecked((int)0xC0000005), code);
        Assert.AreEqual((nuint)0x1000, address);
    }

    [TestMethod]
    public void SelectExceptionRecord_NonStowedCrash_UsesFirstChance()
    {
        var (code, address, useStowed) = CrashDumpService.SelectExceptionRecord(
            savedExceptionCode: unchecked((int)0xE0434352), savedExceptionAddress: 0x1000,
            crashExceptionCode: unchecked((int)0xC0000005), crashExceptionAddress: 0x2000,
            crashExceptionParameters: [0xDEAD, 1]);

        Assert.IsFalse(useStowed, "A non-stowed terminating exception must not replace the record, even with parameters.");
        Assert.AreEqual(unchecked((int)0xE0434352), code);
        Assert.AreEqual((nuint)0x1000, address);
    }

    [TestMethod]
    public void SelectExceptionRecord_SavedRecordIsTheCrash_KeepsCrashParameters()
    {
        var (code, address, useCrashParameters) = CrashDumpService.SelectExceptionRecord(
            savedExceptionCode: unchecked((int)0xC0000409), savedExceptionAddress: 0x2000,
            crashExceptionCode: unchecked((int)0xC0000409), crashExceptionAddress: 0x2000,
            crashExceptionParameters: [7]);

        Assert.IsTrue(useCrashParameters, "When the record describes the crash itself, its parameters (e.g. the fail-fast code) must be kept.");
        Assert.AreEqual(unchecked((int)0xC0000409), code);
        Assert.AreEqual((nuint)0x2000, address);
    }

    // ---- AnalyzeDumpAsync orchestration (via the analyzer seams; no real dump) ----

    [TestMethod]
    public async Task AnalyzeDumpAsync_ManagedSummary_WritesCrashAnalysisAndLog()
    {
        var logPath = Path.Combine(_tempDir, "managed.log");
        _service.ClrMdAnalyzerOverride = (_, _) => ("Managed crash summary", "Managed crash details", false);

        await _service.AnalyzeDumpAsync(Path.Combine(_tempDir, "any.dmp"), logPath);

        var output = _console.Output;
        StringAssert.Contains(output, "CRASH DETECTED");
        StringAssert.Contains(output, "Managed crash summary");
        Assert.IsTrue(File.Exists(logPath));
        StringAssert.Contains(await File.ReadAllTextAsync(logPath), "Managed crash details");
        Assert.AreEqual(0, _xamlTriage.AnalyzeCalls.Count, "Non-WinUI dumps must not trigger triage.");
    }

    [TestMethod]
    public async Task AnalyzeDumpAsync_WinUiManagedTriageSucceeded_ShowsVerdictAndAppendsLog()
    {
        var logPath = Path.Combine(_tempDir, "winui.log");
        _service.ClrMdAnalyzerOverride = (_, _) => ("Managed summary", "Managed details", true);
        _xamlTriage.FakeResult = XamlTriageResult.Succeeded("TRIAGE BREAKDOWN TEXT", "0xC000027B — stowed boom");

        await _service.AnalyzeDumpAsync(Path.Combine(_tempDir, "any.dmp"), logPath, useSymbols: true);

        var output = _console.Output;
        StringAssert.Contains(output, "0xC000027B — stowed boom");
        StringAssert.Contains(output, "written to the debug log");
        StringAssert.Contains(await File.ReadAllTextAsync(logPath), "TRIAGE BREAKDOWN TEXT");
        Assert.AreEqual(1, _xamlTriage.AnalyzeCalls.Count);
        Assert.IsTrue(_xamlTriage.AnalyzeCalls[0].UseSymbols, "useSymbols must flow through to triage.");
    }

    [TestMethod]
    public async Task AnalyzeDumpAsync_WinUiTriageSucceededWithoutVerdict_ShowsLogPointerOnly()
    {
        var logPath = Path.Combine(_tempDir, "winui-noverdict.log");
        _service.ClrMdAnalyzerOverride = (_, _) => ("Managed summary", "Managed details", true);
        _xamlTriage.FakeResult = XamlTriageResult.Succeeded("BREAKDOWN", verdict: null);

        await _service.AnalyzeDumpAsync(Path.Combine(_tempDir, "any.dmp"), logPath);

        var output = _console.Output;
        StringAssert.Contains(output, "written to the debug log");
        Assert.IsFalse(output.Contains("WinUI stowed exception:", StringComparison.Ordinal),
            "No verdict line should be shown when the triage result has no verdict.");
    }

    [TestMethod]
    public async Task AnalyzeDumpAsync_WinUiTriageSkipped_ShowsSkippedNote()
    {
        var logPath = Path.Combine(_tempDir, "skipped.log");
        _service.ClrMdAnalyzerOverride = (_, _) => ("Managed summary", "Managed details", true);
        _xamlTriage.FakeResult = XamlTriageResult.Skipped("tools unavailable");

        await _service.AnalyzeDumpAsync(Path.Combine(_tempDir, "any.dmp"), logPath);

        StringAssert.Contains(_console.Output, "triage was skipped");
    }

    [TestMethod]
    public async Task AnalyzeDumpAsync_NativeFallback_WritesNativeSummaryAndSymbolsTip()
    {
        var logPath = Path.Combine(_tempDir, "native.log");
        // Empty managed summary forces the native DbgEng fallback.
        _service.ClrMdAnalyzerOverride = (_, _) => (string.Empty, "Managed details", false);
        _service.DbgEngAnalyzerOverride = (_, _) => ("Native stack summary", "Native stack details");

        await _service.AnalyzeDumpAsync(Path.Combine(_tempDir, "any.dmp"), logPath, useSymbols: false);

        var output = _console.Output;
        StringAssert.Contains(output, "CRASH ANALYSIS (native)");
        StringAssert.Contains(output, "Native stack summary");
        StringAssert.Contains(output, "Re-run with");
        StringAssert.Contains(await File.ReadAllTextAsync(logPath), "Native stack details");
    }

    [TestMethod]
    public async Task AnalyzeDumpAsync_NativeFallbackWithSymbols_OmitsTipAndShowsDownloadingMessage()
    {
        var logPath = Path.Combine(_tempDir, "native-sym.log");
        _service.ClrMdAnalyzerOverride = (_, _) => (string.Empty, string.Empty, false);
        _service.DbgEngAnalyzerOverride = (_, _) => ("Native summary", "Native details");

        await _service.AnalyzeDumpAsync(Path.Combine(_tempDir, "any.dmp"), logPath, useSymbols: true);

        var output = _console.Output;
        StringAssert.Contains(output, "Downloading symbols");
        Assert.IsFalse(output.Contains("Re-run with", StringComparison.Ordinal),
            "The --symbols tip must not be shown when symbols were already requested.");
    }

    [TestMethod]
    public async Task AnalyzeDumpAsync_AnalyzerThrows_ShowsAnalysisFailed()
    {
        var logPath = Path.Combine(_tempDir, "boom.log");
        _service.ClrMdAnalyzerOverride = (_, _) => throw new InvalidOperationException("analyzer exploded");

        await _service.AnalyzeDumpAsync(Path.Combine(_tempDir, "any.dmp"), logPath);

        StringAssert.Contains(_console.Output, "Analysis failed");
    }

    [TestMethod]
    public async Task AnalyzeDumpAsync_NativeFallbackWithWinUiTriage_AppendsTriageAndShowsVerdict()
    {
        // Empty managed summary → native DbgEng fallback; isWinUi:true → the WinUI triage pass still
        // runs and its log text is appended to the *native* branch's detail block alongside the
        // managed and native details. Exercises the native-path triage append that the managed and
        // non-WinUI native tests never reach.
        var logPath = Path.Combine(_tempDir, "native-winui.log");
        _service.ClrMdAnalyzerOverride = (_, _) => (string.Empty, "MANAGED_DETAILS_TEXT", true);
        _service.DbgEngAnalyzerOverride = (_, _) => ("NATIVE_SUMMARY_TEXT", "NATIVE_DETAILS_TEXT");
        _xamlTriage.FakeResult = XamlTriageResult.Succeeded("TRIAGE_LOG_TEXT", "0xC000027B — stowed verdict");

        await _service.AnalyzeDumpAsync(Path.Combine(_tempDir, "any.dmp"), logPath, useSymbols: false);

        var output = _console.Output;
        StringAssert.Contains(output, "CRASH ANALYSIS (native)");
        StringAssert.Contains(output, "NATIVE_SUMMARY_TEXT");
        StringAssert.Contains(output, "0xC000027B — stowed verdict");
        StringAssert.Contains(output, "written to the debug log");

        var log = await File.ReadAllTextAsync(logPath);
        StringAssert.Contains(log, "MANAGED_DETAILS_TEXT");
        StringAssert.Contains(log, "NATIVE_DETAILS_TEXT");
        StringAssert.Contains(log, "TRIAGE_LOG_TEXT");
        Assert.AreEqual(1, _xamlTriage.AnalyzeCalls.Count, "WinUI dumps must invoke triage on the native path too.");
    }

    [TestMethod]
    public async Task AnalyzeDumpAsync_WinUiTriageNone_ShowsNoTriageConsoleLine()
    {
        // isWinUi:true but the triage service returns the sentinel None result (no verdict, no log
        // text). WriteXamlTriageConsole's None branch is a no-op: neither the success pointer nor the
        // skipped note is printed, and nothing extra is appended to the log.
        var logPath = Path.Combine(_tempDir, "winui-none.log");
        _service.ClrMdAnalyzerOverride = (_, _) => ("MANAGED_SUMMARY_TEXT", "MANAGED_DETAILS_TEXT", true);
        _xamlTriage.FakeResult = XamlTriageResult.None;

        await _service.AnalyzeDumpAsync(Path.Combine(_tempDir, "any.dmp"), logPath);

        var output = _console.Output;
        StringAssert.Contains(output, "MANAGED_SUMMARY_TEXT");
        StringAssert.Contains(output, "CRASH DETECTED");
        Assert.IsFalse(output.Contains("written to the debug log", StringComparison.Ordinal),
            "A None triage result must not claim triage was written to the log.");
        Assert.IsFalse(output.Contains("triage was skipped", StringComparison.Ordinal),
            "A None triage result must not show the skipped note.");
        Assert.AreEqual(1, _xamlTriage.AnalyzeCalls.Count, "WinUI dumps must still invoke triage.");
    }

    // ---- Pure helpers ----

    [TestMethod]
    public void AppendStackOverflowSummary_CollapsesConsecutiveRepeats()
    {
        var frames = new List<(string, string?)>
        {
            ("Rec.Loop", null), ("Rec.Loop", null), ("Rec.Loop", null),
            ("Rec.Other", null), ("Rec.Final", null),
        };
        var sb = new StringBuilder();

        CrashDumpService.AppendStackOverflowSummary(1234, frames, f => f.Item1, f => f.Item2, sb);

        var text = sb.ToString();
        StringAssert.Contains(text, "Stack Overflow (deep recursion detected)");
        StringAssert.Contains(text, "Thread: 1234 (5 managed frames)");
        StringAssert.Contains(text, "Rec.Loop");
        StringAssert.Contains(text, "repeated 2 more times");
        StringAssert.Contains(text, "Rec.Other");
        StringAssert.Contains(text, "Rec.Final");
    }

    [TestMethod]
    public void AppendStackOverflowSummary_WithSource_FormatsFrameInFile()
    {
        var frames = new List<(string, string?)> { ("A.B", "file.cs:42") };
        var sb = new StringBuilder();

        CrashDumpService.AppendStackOverflowSummary(7, frames, f => f.Item1, f => f.Item2, sb);

        StringAssert.Contains(sb.ToString(), "A.B in file.cs:42");
    }

    [TestMethod]
    public void AppendStackOverflowSummary_CapsDisplayedFramesAt15()
    {
        // 30 distinct frames — only the first 15 should be displayed.
        var frames = Enumerable.Range(0, 30).Select(i => ($"Frame.M{i}", (string?)null)).ToList();
        var sb = new StringBuilder();

        CrashDumpService.AppendStackOverflowSummary(9, frames, f => f.Item1, f => f.Item2, sb);

        var text = sb.ToString();
        StringAssert.Contains(text, "Frame.M14");
        Assert.IsFalse(text.Contains("Frame.M15", StringComparison.Ordinal),
            "Frames beyond the 15-frame display cap must be omitted from the summary.");
    }

    [TestMethod]
    public void AppendStackOverflowSummary_TrailingRepeatedRunEmitsSummaryLine()
    {
        // A repeated run at the very end (loop exhausts with repeatCount > 0, well under the cap)
        // must still emit the trailing "... (repeated N more times)" line.
        var frames = new List<(string, string?)>
        {
            ("Rec.Head", null), ("Rec.Tail", null), ("Rec.Tail", null), ("Rec.Tail", null),
        };
        var sb = new StringBuilder();

        CrashDumpService.AppendStackOverflowSummary(55, frames, f => f.Item1, f => f.Item2, sb);

        var text = sb.ToString();
        StringAssert.Contains(text, "Rec.Head");
        StringAssert.Contains(text, "Rec.Tail");
        StringAssert.Contains(text, "repeated 2 more times");
    }

    [TestMethod]
    public void AppendStackOverflowSummary_RepeatedRunReachingCapStopsBeforeNextFrame()
    {
        // 14 distinct frames fill the display, then a repeated run of the 14th followed by a new
        // distinct frame: emitting the "repeated" line reaches the 15-line cap, so the loop breaks
        // before the following distinct frame is shown.
        var frames = new List<(string, string?)>();
        for (var i = 0; i < 14; i++)
        {
            frames.Add(($"Fr.M{i}", null));
        }

        frames.Add(("Fr.M13", null)); // repeat of the 14th distinct frame
        frames.Add(("Fr.M13", null));
        frames.Add(("Fr.After", null)); // distinct frame that must NOT be displayed

        var sb = new StringBuilder();

        CrashDumpService.AppendStackOverflowSummary(77, frames, f => f.Item1, f => f.Item2, sb);

        var text = sb.ToString();
        StringAssert.Contains(text, "Fr.M13");
        StringAssert.Contains(text, "repeated 2 more times");
        Assert.IsFalse(text.Contains("Fr.After", StringComparison.Ordinal),
            "The frame after the display cap was reached must be omitted from the summary.");
    }

    [TestMethod]
    public void IsWinUiModuleFileName_MatchesRegardlessOfCaseAndDirectory()
    {
        Assert.IsTrue(CrashDumpService.IsWinUiModuleFileName(@"C:\app\Microsoft.UI.Xaml.dll"));
        Assert.IsTrue(CrashDumpService.IsWinUiModuleFileName("microsoft.ui.xaml.DLL"));
        Assert.IsFalse(CrashDumpService.IsWinUiModuleFileName(@"C:\app\kernel32.dll"));
        Assert.IsFalse(CrashDumpService.IsWinUiModuleFileName(null));
    }

    [TestMethod]
    public void EnumerateHasWinUiModule_DetectsPresenceAndAbsence()
    {
        Assert.IsTrue(CrashDumpService.EnumerateHasWinUiModule(
            [@"C:\a\ntdll.dll", null, @"C:\a\Microsoft.UI.Xaml.dll"]));
        Assert.IsFalse(CrashDumpService.EnumerateHasWinUiModule(
            [@"C:\a\ntdll.dll", @"C:\a\kernel32.dll"]));
    }

    [TestMethod]
    public void ParseStackModuleNames_ParsesModulesAndSkipsHexAndMangled()
    {
        var stack =
            "00 0014f6a8 ntdll!NtWaitForSingleObject+0x14\n" +
            "01 0014f6b0 KERNELBASE!WaitForSingleObjectEx+0x8e\n" +
            "02 0014f6c0 Microsoft_UI_Xaml+0x3e503\n" +
            "03 0014f6d0 0x7ff8abcd1234\n" +
            "04 0014f6e0 mangled`anonymous+0x5\n";

        var modules = CrashDumpService.ParseStackModuleNames(stack);

        CollectionAssert.Contains(modules.ToList(), "ntdll");
        CollectionAssert.Contains(modules.ToList(), "KERNELBASE");
        CollectionAssert.Contains(modules.ToList(), "Microsoft_UI_Xaml");
        Assert.IsFalse(modules.Any(m => m.Contains('`')), "Mangled (backtick) names must be skipped.");
        Assert.IsFalse(modules.Any(m => m.StartsWith("0x", StringComparison.OrdinalIgnoreCase)), "Hex addresses must be skipped.");
    }

    [TestMethod]
    public void ExtractImagePath_FindsImagePathLine()
    {
        var lmvm =
            "Loaded symbol image file: kernel32.dll\n" +
            "    Image path: C:\\Windows\\System32\\kernel32.dll\n" +
            "    Image name: kernel32.dll\n";

        Assert.AreEqual(@"C:\Windows\System32\kernel32.dll", CrashDumpService.ExtractImagePath(lmvm));
    }

    [TestMethod]
    public void ExtractImagePath_NoImagePath_ReturnsNull()
    {
        Assert.IsNull(CrashDumpService.ExtractImagePath("Image name: kernel32.dll\nsome other line"));
    }

    [TestMethod]
    public void ExtractNativeStackSummary_ParsesFramesStripsParamsAndCaps()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Some preamble that is ignored");
        sb.AppendLine("Child-SP          RetAddr           Call Site");
        for (var i = 0; i < 20; i++)
        {
            sb.AppendLine($"0014f6a{i:X} 00007ff8`0000000{i:X} module!Func{i}(int, char)+0x{i:X}");
        }

        var summary = CrashDumpService.ExtractNativeStackSummary(sb.ToString());

        StringAssert.Contains(summary, "Stack:");
        StringAssert.Contains(summary, "module!Func0");
        Assert.IsFalse(summary.Contains("(int, char)", StringComparison.Ordinal), "Parameters after '(' must be stripped.");
        StringAssert.Contains(summary, "more frames in log");
    }

    [TestMethod]
    public void ExtractNativeStackSummary_NoChildSpHeader_ReturnsEmpty()
    {
        Assert.AreEqual(string.Empty, CrashDumpService.ExtractNativeStackSummary("no stack header here\njust text"));
    }

    [TestMethod]
    public void ExtractNativeStackSummary_TruncatesLongTemplateCallSite()
    {
        var longTemplate = "moduleNameHere!Namespace::Class<" + new string('X', 200) + ">::Method+0x10";
        var input = "Child-SP RetAddr Call Site\n0014 00007ff8 " + longTemplate + "\n";

        var summary = CrashDumpService.ExtractNativeStackSummary(input);

        StringAssert.Contains(summary, "<...>");
        Assert.IsFalse(summary.Contains(new string('X', 200), StringComparison.Ordinal),
            "Long template arguments must be collapsed to <...>.");
    }

    [TestMethod]
    public void ExtractNativeStackSummary_TruncatesLongNonTemplateCallSite()
    {
        // A call site longer than 100 chars that has no '<' template marker takes the non-template
        // truncation branch: the first 100 characters are kept and an ellipsis is appended (rather
        // than the "<...>" collapse used for templated names).
        var longCallSite = "mod!" + new string('a', 150);
        var input = "Child-SP RetAddr Call Site\n0014 00007ff8 " + longCallSite + "\n";

        var summary = CrashDumpService.ExtractNativeStackSummary(input);

        StringAssert.Contains(summary, "mod!");
        StringAssert.Contains(summary, "...");
        Assert.IsFalse(summary.Contains(new string('a', 120), StringComparison.Ordinal),
            "A non-template call site over 100 chars must be truncated to 100 chars + ellipsis.");
        Assert.IsFalse(summary.Contains("more frames in log", StringComparison.Ordinal),
            "A single frame must not trigger the frame-cap message.");
    }

    [TestMethod]
    public void ValidatePdbMatchesDll_MissingDll_AcceptsByName()
    {
        Assert.IsTrue(CrashDumpService.ValidatePdbMatchesDll(
            Path.Combine(_tempDir, "nonexistent.dll"), Path.Combine(_tempDir, "nonexistent.pdb")));
    }

    [TestMethod]
    public void ValidatePdbMatchesDll_MatchingPdb_ReturnsTrue()
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "winapp.dll");
        var pdb = Path.Combine(AppContext.BaseDirectory, "winapp.pdb");
        if (!File.Exists(dll) || !File.Exists(pdb))
        {
            Assert.Inconclusive("winapp.dll/.pdb not present alongside the test binaries.");
            return;
        }

        Assert.IsTrue(CrashDumpService.ValidatePdbMatchesDll(dll, pdb));
    }

    [TestMethod]
    public void ValidatePdbMatchesDll_MismatchedPdb_ReturnsFalse()
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "winapp.dll");
        var wrongPdb = Path.Combine(AppContext.BaseDirectory, "WinApp.Cli.Tests.pdb");
        if (!File.Exists(dll) || !File.Exists(wrongPdb))
        {
            Assert.Inconclusive("Required dll/pdb pair not present alongside the test binaries.");
            return;
        }

        Assert.IsFalse(CrashDumpService.ValidatePdbMatchesDll(dll, wrongPdb),
            "A PDB whose GUID does not match the DLL's CodeView GUID must be rejected.");
    }

    [TestMethod]
    public void ValidatePdbMatchesDll_MissingPdbButDllHasCodeView_AcceptsByName()
    {
        // DLL exists with a CodeView entry, but the PDB path is missing → File.OpenRead throws →
        // the catch accepts by name (returns true).
        var dll = Path.Combine(AppContext.BaseDirectory, "winapp.dll");
        if (!File.Exists(dll))
        {
            Assert.Inconclusive("winapp.dll not present alongside the test binaries.");
            return;
        }

        Assert.IsTrue(CrashDumpService.ValidatePdbMatchesDll(dll, Path.Combine(_tempDir, "missing.pdb")));
    }

    [TestMethod]
    public void AppendStackOverflowSummary_ResolvesSourceLazily_AndStopsAtDisplayCap()
    {
        // Regression guard (H1): source resolution must stay lazy — resolved per frame as the loop
        // visits it and stopped the moment the 15-frame display cap is reached — never eagerly resolving
        // every frame of a deep (potentially many-thousand-frame) overflow. A poison frame far beyond the
        // cap throws if it is ever touched, proving frames past #15 are never resolved.
        var frames = Enumerable.Range(0, 50).Select(i => $"Frame.M{i}").ToList();
        var resolveCount = 0;
        var summary = new StringBuilder();

        CrashDumpService.AppendStackOverflowSummary(
            osThreadId: 42,
            frames,
            nameSelector: f => f,
            sourceResolver: f =>
            {
                resolveCount++;
                if (f == "Frame.M30")
                {
                    throw new InvalidOperationException(
                        "source must never be resolved for frames beyond the 15-frame display cap");
                }

                return null;
            },
            summary);

        Assert.AreEqual(15, resolveCount,
            "With distinct frames the loop must resolve source for exactly the 15 displayed frames, then stop.");
        var text = summary.ToString();
        StringAssert.Contains(text, "Frame.M14");
        Assert.IsFalse(text.Contains("Frame.M15", StringComparison.Ordinal),
            "Frames beyond the 15-frame display cap must never be resolved or displayed.");
        StringAssert.Contains(text, "42 (50 managed frames)");
    }
}
