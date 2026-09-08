// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

/// <summary>
/// CLI presentation for the UI Automation library's pre-injection guards. The library decides
/// whether a gesture may proceed; these cover the text/<c>--json</c> errors the CLI emits for each
/// outcome.
/// </summary>
[TestClass]
public class UiInjectionReportingTests
{
    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add((logLevel, formatter(state, exception)));
    }

    private sealed class StubGuard(ForegroundCheck outcome) : IForegroundGuard
    {
        public ForegroundCheck CheckForeground(long targetHwnd) => outcome;
        public bool IsRemoteSession() => false;
    }

    private static UiElement Element()
        => new() { X = 0, Y = 0, Width = 10, Height = 10, Type = "Button", Name = "OK" };

    [TestMethod]
    public void TryEnsureForeground_ProceedsWithoutLogging()
    {
        var logger = new CapturingLogger();

        Assert.IsTrue(new StubGuard(ForegroundCheck.Proceed).TryEnsureForeground(10, logger, json: false, action: "click"));
        Assert.AreEqual(0, logger.Messages.Count);
    }

    [TestMethod]
    public void TryEnsureForeground_ReportsNoInteractiveDesktop()
    {
        var logger = new CapturingLogger();

        Assert.IsFalse(new StubGuard(ForegroundCheck.NoInteractiveDesktop).TryEnsureForeground(10, logger, json: false, action: "drag"));

        Assert.AreEqual(LogLevel.Error, logger.Messages.Single().Level);
        StringAssert.Contains(logger.Messages.Single().Message, "No interactive desktop");
    }

    [TestMethod]
    public void TryEnsureForeground_ReportsForegroundNotTargetWithAction()
    {
        var logger = new CapturingLogger();

        Assert.IsFalse(new StubGuard(ForegroundCheck.ForegroundNotTarget).TryEnsureForeground(10, logger, json: false, action: "scroll --wheel"));

        Assert.AreEqual(LogLevel.Error, logger.Messages.Single().Level);
        StringAssert.Contains(logger.Messages.Single().Message, "refusing to scroll --wheel");
    }

    [TestMethod]
    public void TryReport_ReturnsTrueForOkWithoutLogging()
    {
        var logger = new CapturingLogger();
        var result = UiInjectionReporting.TryReport(
            new StableTarget(TargetStatus.Ok, Element(), 5, 5), logger, json: false, selector: "btn", action: "click");

        Assert.IsTrue(result);
        Assert.AreEqual(0, logger.Messages.Count);
    }

    [TestMethod]
    [DataRow((int)TargetStatus.ZeroSize, "collapsed to zero size")]
    [DataRow((int)TargetStatus.NotFound, "could not be re-resolved")]
    [DataRow((int)TargetStatus.Moving, "still moving/resizing")]
    public void TryReport_LogsSpecificAbortReason(int statusValue, string expectedMessage)
    {
        var logger = new CapturingLogger();

        var result = UiInjectionReporting.TryReport(
            new StableTarget((TargetStatus)statusValue, Element(), 5, 5), logger, json: false, selector: "btn", action: "drag");

        Assert.IsFalse(result);
        StringAssert.Contains(logger.Messages.Single().Message, expectedMessage);
        StringAssert.Contains(logger.Messages.Single().Message, "drag");
    }
}
