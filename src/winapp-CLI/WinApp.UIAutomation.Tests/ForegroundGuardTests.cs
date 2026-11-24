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
    [DataRow(10L, 10L, 20L, ForegroundCheck.Proceed)]
    [DataRow(20L, 20L, 20L, ForegroundCheck.Proceed)]
    [DataRow(20L, 30L, 40L, ForegroundCheck.ForegroundNotTarget)]
    [DataRow(0L, 0L, 40L, ForegroundCheck.NoInteractiveDesktop)]
    [DataRow(0L, 20L, 40L, ForegroundCheck.ForegroundNotTarget)]
    [DataRow(20L, 0L, 40L, ForegroundCheck.NoInteractiveDesktop)]
    [DataRow(10L, 0L, 40L, ForegroundCheck.Proceed)]
    public void CheckForeground_SnapshotRetainsOriginalReadsAndDecision(
        long firstForeground, long secondForeground, long rootHwnd, ForegroundCheck expected)
    {
        var calls = new List<string>();
        ConfigureReads();
        Assert.AreEqual(expected, ForegroundGuard.CheckForeground(10));
        var originalCalls = calls.ToArray();

        calls.Clear();
        ConfigureReads();
        var result = new RealForegroundGuard().CheckForeground(10, out var snapshot);

        Assert.AreEqual(expected, result);
        Assert.AreSequenceEqual(originalCalls, calls.Take(originalCalls.Length).ToArray(),
            "Diagnostics must preserve both foreground reads and the comparison's root lookup order.");
        Assert.AreEqual(2, calls.Count(c => c.StartsWith("foreground", StringComparison.Ordinal)));
        if (expected == ForegroundCheck.Proceed)
        {
            Assert.IsNull(snapshot);
            Assert.AreSequenceEqual(originalCalls, calls, "Accepted checks need no diagnostic ownership queries.");
            return;
        }

        Assert.IsNotNull(snapshot);
        ForegroundGuard.s_getForegroundWindow = () => throw new AssertFailedException("Do not re-read foreground for retained diagnostics.");
        Assert.AreEqual(firstForeground, snapshot.ForegroundHwnd);
        Assert.AreEqual(secondForeground, snapshot.ClassificationForegroundHwnd);
        Assert.AreEqual(rootHwnd, snapshot.RootHwnd);
        Assert.AreEqual(firstForeground != 0, snapshot.RootWasCompared);
        Assert.AreEqual(110u, snapshot.TargetProcessId);
        Assert.AreEqual((uint)(rootHwnd + 100), snapshot.RootProcessId);
        Assert.AreEqual(firstForeground == 0 ? 0u : (uint)(firstForeground + 100), snapshot.ForegroundProcessId);
        Assert.AreEqual(secondForeground == 0 ? 0u : (uint)(secondForeground + 100), snapshot.ClassificationForegroundProcessId);

        void ConfigureReads()
        {
            var foregrounds = new Queue<long>([firstForeground, secondForeground]);
            ForegroundGuard.s_getForegroundWindow = () =>
            {
                var hwnd = foregrounds.Dequeue();
                calls.Add($"foreground:{hwnd}");
                return new HWND((nint)hwnd);
            };
            ForegroundGuard.s_getRootAncestor = hwnd =>
            {
                calls.Add($"root:{(nint)hwnd}");
                return new HWND((nint)rootHwnd);
            };
            ForegroundGuard.s_getProcessIdForWindow = hwnd =>
            {
                calls.Add($"pid:{hwnd}");
                return (uint)(hwnd + 100);
            };
        }
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
