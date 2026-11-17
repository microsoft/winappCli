// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class XamlTriageResultTests
{
    [TestMethod]
    public void Succeeded_CarriesLogTextAndVerdict()
    {
        var result = XamlTriageResult.Succeeded("full breakdown", "0xc000027b — boom");

        Assert.AreEqual(XamlTriageOutcome.Succeeded, result.Outcome);
        Assert.AreEqual("full breakdown", result.LogText);
        Assert.AreEqual("0xc000027b — boom", result.Verdict);
    }

    [TestMethod]
    public void Succeeded_AllowsNullVerdict()
    {
        var result = XamlTriageResult.Succeeded("breakdown only", null);

        Assert.AreEqual(XamlTriageOutcome.Succeeded, result.Outcome);
        Assert.AreEqual("breakdown only", result.LogText);
        Assert.IsNull(result.Verdict);
    }

    [TestMethod]
    public void Skipped_RecordsNoteWithoutVerdict()
    {
        var result = XamlTriageResult.Skipped("WinUI Triage: skipped — tooling unavailable.");

        Assert.AreEqual(XamlTriageOutcome.Skipped, result.Outcome);
        Assert.AreEqual("WinUI Triage: skipped — tooling unavailable.", result.LogText);
        Assert.IsNull(result.Verdict, "A skip records only an explanatory note, never a verdict.");
    }

    [TestMethod]
    public void None_HasNoTextOrVerdict()
    {
        Assert.AreEqual(XamlTriageOutcome.None, XamlTriageResult.None.Outcome);
        Assert.IsNull(XamlTriageResult.None.LogText);
        Assert.IsNull(XamlTriageResult.None.Verdict);
    }

    [TestMethod]
    public void None_IsSingletonInstance()
    {
        var firstAccess = XamlTriageResult.None;
        Assert.AreSame(firstAccess, XamlTriageResult.None,
            "None is exposed as a shared instance property, not a fresh allocation per access.");
    }
}
