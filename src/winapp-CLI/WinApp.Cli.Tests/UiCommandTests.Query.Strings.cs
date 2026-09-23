// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;
using WinApp.Cli.Commands;

namespace WinApp.Cli.Tests;

public partial class UiCommandTests
{
    [TestMethod]
    [DataRow("UIA_NamePropertyId", unchecked((int)0x80040201))]
    [DataRow("UIA_NamePropertyId", unchecked((int)0x80004005))]
    [DataRow("UIA_AutomationIdPropertyId", unchecked((int)0x80040201))]
    [DataRow("UIA_AutomationIdPropertyId", unchecked((int)0x80004005))]
    [DataRow("UIA_ClassNamePropertyId", unchecked((int)0x80040201))]
    [DataRow("UIA_ClassNamePropertyId", unchecked((int)0x80004005))]
    [DataRow("UIA_NamePropertyId", unchecked((int)0x80040201), 1100)]
    public async Task QueryOptions_RealGetterFailureIsNeverGone(string property, int hresult, int firstReadDelayMs = 0)
    {
        if (!Environment.UserInteractive) { Assert.Inconclusive("Requires an interactive desktop."); }
        using var fx = new UiaTestFixture();
        fx.OnUiThread(() => fx.ValueBox.AccessibleName = "query-name");
        var svc = RealStringQueryService(fx);
        var nativeGetter = UiAutomationService.s_getCurrentBstr;
        var reads = 0;
        try
        {
            UiAutomationService.s_getCurrentBstr = (element, requested) =>
            {
                if (requested.ToString() == property)
                {
                    if (++reads == 1 && firstReadDelayMs > 0) { Thread.Sleep(firstReadDelayMs); }
                    throw new COMException("Getter failed.", hresult);
                }
                return nativeGetter(element, requested);
            };
            UiAutomationService.s_findAllDescendants = (_, _) => null;
            var command = RealStringQueryCommand("wait-for", svc);
            var args = new List<string>
            {
                property == "UIA_NamePropertyId" ? "query-name" : "txtValue",
                "-w", fx.Hwnd.ToString(), "--type", "Edit", "--gone", "--timeout", "1000", "--json",
            };
            if (property == "UIA_ClassNamePropertyId") { args.AddRange(["--class-name", ""]); }
            var exit = await ParseAndInvokeWithCaptureAsync(command, args.ToArray());
            Assert.AreEqual(1, exit, TestAnsiConsole.Output);
            if (hresult == unchecked((int)0x80040201))
            {
                StringAssert.Contains(TestAnsiConsole.Output, "\"timedOut\": true");
                Assert.IsTrue(reads > 0, "The query must exercise the failing getter.");
                // A native read can exhaust the timeout before another poll is possible.
                Assert.AreEqual(reads, _fakePollDelay.CallCount,
                    "Every unavailable getter must enter the retry path rather than report absence.");
            }
            else
            {
                AssertJsonErrorCode("stale_element");
                Assert.AreEqual(1, reads, "Arbitrary provider faults must fail immediately.");
                Assert.AreEqual(0, _fakePollDelay.CallCount);
            }
        }
        finally { UiAutomationService.ResetNativeSeams(); }
    }

    [TestMethod]
    [DataRow("search")]
    [DataRow("get-property")]
    [DataRow("get-value")]
    [DataRow("wait-for")]
    [DataRow("gone")]
    public async Task QueryOptions_RealEmptyClassMatchesLiterally(string name)
    {
        if (!Environment.UserInteractive) { Assert.Inconclusive("Requires an interactive desktop."); }
        using var fx = new UiaTestFixture();
        fx.OnUiThread(() => fx.ValueBox.Text = "ready");
        var svc = RealStringQueryService(fx);
        var nativeGetter = UiAutomationService.s_getCurrentBstr;
        try
        {
            UiAutomationService.s_getCurrentBstr = (element, property) =>
                property.ToString() == "UIA_ClassNamePropertyId" ? default : nativeGetter(element, property);
            UiAutomationService.s_findAllDescendants = (_, _) => null;
            var command = RealStringQueryCommand(name == "gone" ? "wait-for" : name, svc);
            var args = new List<string> { "txtValue", "-w", fx.Hwnd.ToString(), "--class-name", "", "--json" };
            if (name == "wait-for") { args.AddRange(["--value", "ready", "--timeout", "500"]); }
            if (name == "gone") { args.AddRange(["--gone", "--timeout", "150"]); }
            var exit = await ParseAndInvokeWithCaptureAsync(command, args.ToArray());
            Assert.AreEqual(name == "gone" ? 1 : 0, exit, $"{TestAnsiConsole.Output} {ConsoleStdErr}");
            if (name == "gone") { StringAssert.Contains(TestAnsiConsole.Output, "\"timedOut\": true"); }
            else { StringAssert.Contains(TestAnsiConsole.Output, "ready"); }
        }
        finally { UiAutomationService.ResetNativeSeams(); }
    }

    private UiAutomationService RealStringQueryService(UiaTestFixture fx)
    {
        _fakeTargetResolver.TargetResult = new UiTarget
        {
            ProcessId = fx.ProcessId, WindowHandle = fx.Hwnd, IsExplicitWindow = true,
            ProcessName = "QueryStringFixture", WindowTitle = fx.Title,
        };
        return new UiAutomationService(GetRequiredService<ILogger<UiAutomationService>>(), new UiSelectorParser());
    }

    private Command RealStringQueryCommand(string name, UiAutomationService svc)
    {
        var command = QueryCommand(name);
        System.CommandLine.Invocation.AsynchronousCommandLineAction handler = name switch
        {
            "search" => new UiSearchCommand.Handler(_fakeTargetResolver, svc, new UiSelectorParser(),
                TestAnsiConsole, _fakeDesktopLock, GetRequiredService<ILogger<UiSearchCommand>>()),
            "get-property" => new UiGetPropertyCommand.Handler(_fakeTargetResolver, svc, new UiSelectorParser(),
                TestAnsiConsole, _fakeDesktopLock, GetRequiredService<ILogger<UiGetPropertyCommand>>()),
            "get-value" => new UiGetValueCommand.Handler(_fakeTargetResolver, svc, new UiSelectorParser(),
                TestAnsiConsole, _fakeDesktopLock, GetRequiredService<ILogger<UiGetValueCommand>>()),
            _ => new UiWaitForCommand.Handler(_fakeTargetResolver, svc, new UiSelectorParser(),
                _fakePollDelay, TestAnsiConsole, _fakeDesktopLock, GetRequiredService<ILogger<UiWaitForCommand>>()),
        };
        command.SetAction((parseResult, ct) => handler.InvokeAsync(parseResult, ct));
        return command;
    }
}
