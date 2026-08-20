// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class XamlTriageServiceTests
{
    // Mirrors the real failure shape when OS symbols for combase.dll are unavailable:
    // the extension loads and detects the stowed exception but cannot expand its structs.
    private const string SymbolGapOutput =
        "************* Symbol Loading Error Summary **************\n" +
        "Module name            Error\n" +
        "combase                The system cannot find the file specified\n" +
        "JavaScript script successfully loaded from 'winui-dbgext.js'\n" +
        "*** WARNING: Symbols for combase.dll not loaded/unavailable.\n" +
        "Error: Error: Invalid argument to method 'createPointerObject' [__Initialize @winui-dbgext (line 3116 col 39)]";

    [TestMethod]
    public void DescribeSymbolGap_WithSymbols_ExplainsServer404()
    {
        var note = XamlTriageService.DescribeSymbolGap(SymbolGapOutput, useSymbols: true);

        Assert.IsNotNull(note);
        StringAssert.Contains(note, "0xC000027B");
        StringAssert.Contains(note, "combase.dll");
        StringAssert.Contains(note, "404");
    }

    [TestMethod]
    public void DescribeSymbolGap_WithoutSymbols_SuggestsSymbolsFlag()
    {
        var note = XamlTriageService.DescribeSymbolGap(SymbolGapOutput, useSymbols: false);

        Assert.IsNotNull(note);
        StringAssert.Contains(note, "--symbols");
    }

    [TestMethod]
    public void DescribeSymbolGap_SuccessfulOutput_ReturnsNull()
    {
        const string goodOutput =
            "-------------------------\n" +
            "Callstack for hr=0x80131509\n" +
            "    winui_app!App.OnLaunched\n" +
            "=========================";

        Assert.IsNull(XamlTriageService.DescribeSymbolGap(goodOutput, useSymbols: true));
    }

    [TestMethod]
    public void DescribeSymbolGap_SymbolsMissingButDecodeSucceeded_ReturnsNull()
    {
        // A symbol warning alone (without the createPointerObject decode failure) is not the gap.
        const string partial =
            "*** WARNING: Symbols for combase.dll not loaded/unavailable.\n" +
            "-------------------------\n" +
            "Callstack for hr=0x80131509";

        Assert.IsNull(XamlTriageService.DescribeSymbolGap(partial, useSymbols: true));
    }

    [TestMethod]
    public void GitBlobSha1_EmptyContent_MatchesGitVector()
    {
        // The well-known git blob hash of empty content: `git hash-object` of an empty file.
        Assert.AreEqual("e69de29bb2d1d6434b8b29ae775ad8c2e48c5391", XamlTriageService.GitBlobSha1([]));
    }

    [TestMethod]
    public void GitBlobSha1_KnownContent_MatchesGitVector()
    {
        // echo -n 'hello' | git hash-object --stdin => b6fc4c620b67d95f953a5c1c1230aaab5db5a1b0
        var bytes = System.Text.Encoding.ASCII.GetBytes("hello");
        Assert.AreEqual("b6fc4c620b67d95f953a5c1c1230aaab5db5a1b0", XamlTriageService.GitBlobSha1(bytes));
    }

    [TestMethod]
    public void MatchesPinnedExtensionHash_TamperedContent_ReturnsFalse()
    {
        // Arbitrary content cannot match the pinned winui-dbgext.js hash, so the integrity gate
        // must reject it (this is the rejection path that protects the debugger from a wrong/tampered script).
        Assert.IsFalse(XamlTriageService.MatchesPinnedExtensionHash(System.Text.Encoding.UTF8.GetBytes("// not the real extension")));
    }

    [TestMethod]
    public void BuildTriageArgs_WithSymbolsAndSymSrv_IncludesSymbolsAndResolvedPaths()
    {
        var binaries = new ResolvedTriageBinaries(@"C:\dbg", @"C:\dbg\winext\JsProvider.dll", HasSymSrv: true, "test");

        var args = XamlTriageService.BuildTriageArgs(@"C:\crash.dmp", binaries, @"C:\ext.js", useSymbols: true);

        Assert.AreEqual(XamlTriageRunner.InternalVerb, args[0]);
        CollectionAssert.Contains(args, "--symbols");
        AssertPairValue(args, "--dump", @"C:\crash.dmp");
        AssertPairValue(args, "--bin", @"C:\dbg");
        AssertPairValue(args, "--jsprovider", @"C:\dbg\winext\JsProvider.dll");
        AssertPairValue(args, "--ext", @"C:\ext.js");
    }

    [TestMethod]
    public void BuildTriageArgs_SymbolsRequestedButNoSymSrv_OmitsSymbols()
    {
        var binaries = new ResolvedTriageBinaries(@"C:\dbg", @"C:\dbg\JsProvider.dll", HasSymSrv: false, "test");

        var args = XamlTriageService.BuildTriageArgs(@"C:\crash.dmp", binaries, @"C:\ext.js", useSymbols: true);

        CollectionAssert.DoesNotContain(args, "--symbols");
    }

    [TestMethod]
    public void BuildTriageArgs_NoSymbols_OmitsSymbols()
    {
        var binaries = new ResolvedTriageBinaries(@"C:\dbg", @"C:\dbg\JsProvider.dll", HasSymSrv: true, "test");

        var args = XamlTriageService.BuildTriageArgs(@"C:\crash.dmp", binaries, @"C:\ext.js", useSymbols: false);

        CollectionAssert.DoesNotContain(args, "--symbols");
    }

    private static void AssertPairValue(List<string> args, string flag, string expected)
    {
        var idx = args.IndexOf(flag);
        Assert.IsTrue(idx >= 0 && idx + 1 < args.Count, $"Expected flag {flag} with a value.");
        Assert.AreEqual(expected, args[idx + 1]);
    }

    [TestMethod]
    public void ShouldPropagateCancellation_InternalHttpTimeout_DoesNotPropagate()
    {
        // HttpClient.Timeout surfaces as a TaskCanceledException whose token is unrelated to the
        // caller's. Swallowing it lets the already-computed managed crash stack survive (regression:
        // an internal first-run download timeout used to discard the ClrMD stack).
        using var caller = new CancellationTokenSource();
        var timeout = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");

        Assert.IsFalse(
            XamlTriageService.ShouldPropagateCancellation(timeout, caller.Token),
            "An internal timeout with a non-cancelled caller token must not propagate out of the fail-open triage pass.");
    }

    [TestMethod]
    public void ShouldPropagateCancellation_CallerCancelled_Propagates()
    {
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        var oce = new OperationCanceledException(caller.Token);

        Assert.IsTrue(
            XamlTriageService.ShouldPropagateCancellation(oce, caller.Token),
            "Genuine caller cancellation must propagate so the run can abort promptly.");
    }

    [TestMethod]
    public void TryExtractVerdict_CodeAndMessage_ReturnsCompactVerdict()
    {
        var output =
            "Stowed Exception found\n" +
            "Error Code: 0x80004005\n" +
            "Error Message: The parameter is incorrect.\n" +
            "Stack:\n";

        var verdict = XamlTriageService.TryExtractVerdict(output);

        Assert.AreEqual("0x80004005 — The parameter is incorrect.", verdict);
    }

    [TestMethod]
    public void TryExtractVerdict_CodeOnly_ReturnsCode()
    {
        Assert.AreEqual("0xC000027B", XamlTriageService.TryExtractVerdict("HRESULT = 0xC000027B\nsome stack"));
    }

    [TestMethod]
    public void TryExtractVerdict_NoRecognizableFields_ReturnsNull()
    {
        Assert.IsNull(XamlTriageService.TryExtractVerdict("just a native stack with no error code"));
        Assert.IsNull(XamlTriageService.TryExtractVerdict(""));
    }

    [TestMethod]
    public void TryExtractVerdict_ResourcePropertyFailure_ReturnsMigrationHint()
    {
        var output = """
            Callstack for hr=80004005
            Microsoft_UI_Xaml!XamlSchemaContext::GetXamlProperty
            Microsoft_UI_Xaml!ObjectWriterRuntime::GetResourcePropertyBagImpl
            Error message: XAML parsing failed.
            """;

        var verdict = XamlTriageService.TryExtractVerdict(output);

        Assert.AreEqual(
            "0x80004005 — XAML parsing failed. — XAML resource property resolution failed; inspect x:Uid/.resw property keys.",
            verdict);
    }
}
