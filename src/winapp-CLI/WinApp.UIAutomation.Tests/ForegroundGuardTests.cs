// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Windows.Win32.Foundation;

using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

[TestClass]
[DoNotParallelize]
public class ForegroundGuardTests
{
    [TestInitialize]
    public void Initialize() => ResetSeams();

    [TestCleanup]
    public void Cleanup() => ForegroundGuard.ResetNativeSeams();

    [TestMethod]
    public void ForegroundBelongsTo_ReturnsFalseForZeroNullForegroundOrUnrelatedRoot()
    {
        Assert.IsFalse(ForegroundGuard.ForegroundBelongsTo(0));

        ForegroundGuard.s_getForegroundWindow = () => HWND.Null;
        Assert.IsFalse(ForegroundGuard.ForegroundBelongsTo(10));

        ForegroundGuard.s_getForegroundWindow = () => new HWND(20);
        ForegroundGuard.s_getRootAncestor = _ => HWND.Null;
        Assert.IsFalse(ForegroundGuard.ForegroundBelongsTo(10));

        ForegroundGuard.s_getRootAncestor = _ => new HWND(30);
        Assert.IsFalse(ForegroundGuard.ForegroundBelongsTo(10));
    }

    [TestMethod]
    public void ForegroundBelongsTo_AcceptsExactTargetOrRootAncestor()
    {
        ForegroundGuard.s_getForegroundWindow = () => new HWND(10);
        Assert.IsTrue(ForegroundGuard.ForegroundBelongsTo(10));

        ForegroundGuard.s_getForegroundWindow = () => new HWND(20);
        ForegroundGuard.s_getRootAncestor = hwnd =>
        {
            Assert.AreEqual((nint)10, (nint)hwnd);
            return new HWND(20);
        };

        Assert.IsTrue(ForegroundGuard.ForegroundBelongsTo(10));
    }

    [TestMethod]
    public void NoInteractiveDesktop_ReflectsNullForeground()
    {
        ForegroundGuard.s_getForegroundWindow = () => HWND.Null;
        Assert.IsTrue(ForegroundGuard.NoInteractiveDesktop());

        ForegroundGuard.s_getForegroundWindow = () => new HWND(1);
        Assert.IsFalse(ForegroundGuard.NoInteractiveDesktop());
    }

    [TestMethod]
    public void Classify_CoversAllCombinations()
    {
        Assert.AreEqual(ForegroundCheck.Proceed, ForegroundGuard.Classify(hasTarget: false, targetIsForeground: false, anyForegroundWindow: false));
        Assert.AreEqual(ForegroundCheck.Proceed, ForegroundGuard.Classify(hasTarget: true, targetIsForeground: true, anyForegroundWindow: false));
        Assert.AreEqual(ForegroundCheck.NoInteractiveDesktop, ForegroundGuard.Classify(hasTarget: true, targetIsForeground: false, anyForegroundWindow: false));
        Assert.AreEqual(ForegroundCheck.ForegroundNotTarget, ForegroundGuard.Classify(hasTarget: true, targetIsForeground: false, anyForegroundWindow: true));
    }

    [TestMethod]
    public void CheckForeground_ProceedsWhenNoTargetOrTargetMatches()
    {
        ForegroundGuard.s_getForegroundWindow = () => new HWND(10);

        Assert.AreEqual(ForegroundCheck.Proceed, ForegroundGuard.CheckForeground(0));
        Assert.AreEqual(ForegroundCheck.Proceed, ForegroundGuard.CheckForeground(10));
    }

    [TestMethod]
    public void CheckForeground_ReportsNoInteractiveDesktop()
    {
        ForegroundGuard.s_getForegroundWindow = () => HWND.Null;

        Assert.AreEqual(ForegroundCheck.NoInteractiveDesktop, ForegroundGuard.CheckForeground(10));
    }

    [TestMethod]
    public void CheckForeground_ReportsForegroundNotTarget()
    {
        ForegroundGuard.s_getForegroundWindow = () => new HWND(20);
        ForegroundGuard.s_getRootAncestor = _ => new HWND(30);

        Assert.AreEqual(ForegroundCheck.ForegroundNotTarget, ForegroundGuard.CheckForeground(10));
    }

    [TestMethod]
    public void RealForegroundGuard_DelegatesToForegroundGuard()
    {
        ForegroundGuard.s_getForegroundWindow = () => new HWND(1);

        // targetHwnd 0 => no target to verify; with a live foreground window present the guard
        // classifies Proceed. Matching that proves the adapter forwarded to ForegroundGuard.
        var result = new RealForegroundGuard().CheckForeground(0);

        Assert.AreEqual(ForegroundCheck.Proceed, result,
            "RealForegroundGuard must delegate to ForegroundGuard.CheckForeground and return its result.");
    }

    private static void ResetSeams()
    {
        ForegroundGuard.s_getForegroundWindow = () => new HWND(1);
        ForegroundGuard.s_getRootAncestor = _ => HWND.Null;
    }
}

internal sealed class CapturingLogger : ILogger
{
    public List<(LogLevel Level, string Message)> Messages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Messages.Add((logLevel, formatter(state, exception)));

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
