// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.InteractiveDesktop;

namespace WinApp.Cli.Tests;

[TestClass]
public class UiCoordinationWaitReporterTests
{
    private static readonly UiCoordinationOutputMode Text = new(Json: false, Verbose: false, Quiet: false);

    private static UiWaitDiagnostics OtherWorkflow(long? heldForMs = 134_000) => new(
        QueueDepth: 1,
        CommandsAhead: 1,
        ActiveProcessId: 424242,
        ActiveOperation: "ui record",
        Reason: UiWaitReason.OtherWorkflowActive,
        HeldForMs: heldForMs);

    private static (UiCoordinationWaitReporter Reporter, StringWriter Output) Create(UiCoordinationOutputMode mode)
    {
        var output = new StringWriter();
        return (new UiCoordinationWaitReporter(output, mode, "ui click"), output);
    }

    [TestMethod]
    public void StaysSilentForTheFirstSecond()
    {
        var (reporter, output) = Create(Text);

        reporter.ReportIfDue(UiCoordinationWaitReporter.FirstReportAfterMs - 1, OtherWorkflow());

        Assert.AreEqual("", output.ToString());
    }

    [TestMethod]
    public void RepeatsNoMoreThanOncePerInterval()
    {
        var (reporter, output) = Create(Text);

        reporter.ReportIfDue(1_000, OtherWorkflow());
        reporter.ReportIfDue(2_000, OtherWorkflow());
        reporter.ReportIfDue(6_000, OtherWorkflow());

        Assert.AreEqual(2, output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public void StaysSilentUnderJsonAndQuiet(bool json, bool quiet)
    {
        var (reporter, output) = Create(new UiCoordinationOutputMode(json, Verbose: false, quiet));

        reporter.ReportIfDue(10_000, OtherWorkflow());

        Assert.AreEqual("", output.ToString());
    }

    [TestMethod]
    public void OtherWorkflow_SaysWhatHoldsTheDesktopForHowLongAndHowToShare()
    {
        var (reporter, output) = Create(Text);

        reporter.ReportIfDue(3_000, OtherWorkflow());

        var line = output.ToString();
        StringAssert.Contains(line, "'ui click' has waited 3s for the desktop");
        StringAssert.Contains(line, "another workflow is using the desktop ('ui record') and has held it for 2m 14s.");
        StringAssert.Contains(line, UiCoordinationWaitReporter.SharingAdvice);
        StringAssert.Contains(line, "Ctrl+C");
        Assert.IsFalse(line.Contains("424242", StringComparison.Ordinal), "the PID is verbose-only");
    }

    [TestMethod]
    public void Verbose_AddsLocalDiagnostics()
    {
        var (reporter, output) = Create(new UiCoordinationOutputMode(Json: false, Verbose: true, Quiet: false));

        reporter.ReportIfDue(3_000, OtherWorkflow());

        StringAssert.Contains(output.ToString(), "held by winapp PID 424242");
        StringAssert.Contains(output.ToString(), "queue depth 1");
    }

    [TestMethod]
    public void Grace_SaysTheHolderMayContinue()
    {
        var (reporter, _) = Create(Text);

        var line = reporter.BuildLine(1_500, new UiWaitDiagnostics(
            1, 0, null, null, UiWaitReason.OtherWorkflowGrace, GraceRemainingMs: 2_500));

        StringAssert.Contains(line, "keeps the desktop for up to 2s more in case it continues");
        Assert.IsFalse(line.Contains("WINAPP_UI_WORKFLOW_ID", StringComparison.Ordinal));
    }

    [TestMethod]
    public void OwnWorkflow_NamesTheEarlierCommandWithoutSharingAdvice()
    {
        var (reporter, _) = Create(Text);

        var line = reporter.BuildLine(1_500, new UiWaitDiagnostics(
            0, 1, null, "ui screenshot", UiWaitReason.OwnWorkflow, WaitersAhead: 3));

        StringAssert.Contains(line, "an earlier 'ui screenshot' in this workflow must finish first.");
        Assert.IsFalse(line.Contains("WINAPP_UI_WORKFLOW_ID", StringComparison.Ordinal));
        Assert.IsFalse(line.Contains("queued ahead", StringComparison.Ordinal));
    }

    [TestMethod]
    public void QueuedBehindOthers_CountsThem()
    {
        var (reporter, _) = Create(Text);

        var line = reporter.BuildLine(1_500, OtherWorkflow() with { WaitersAhead = 2 });

        StringAssert.Contains(line, "2 commands from other workflows are queued ahead of this one.");
    }

    [TestMethod]
    [DataRow(0L, "0s")]
    [DataRow(59_999L, "59s")]
    [DataRow(60_000L, "1m 0s")]
    [DataRow(3_900_000L, "1h 5m")]
    public void FormatsDurations(long ms, string expected)
        => Assert.AreEqual(expected, UiCoordinationWaitReporter.FormatDuration(ms));
}
